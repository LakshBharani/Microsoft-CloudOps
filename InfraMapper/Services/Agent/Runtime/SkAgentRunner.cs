using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class SkAgentRunner
{
    private const int DefaultTimeoutSeconds = 90;

    public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        ChatCompletionAgent agent,
        string message,
        SkAgentSession session,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var channel = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
        var kernel = agent.Kernel.Clone();
        var filter = new RecordingAutoFunctionInvocationFilter(evt => channel.Writer.TryWrite(evt));
        kernel.AutoFunctionInvocationFilters.Add(filter);

        var arguments = BuildArguments();
        var options = new AgentInvokeOptions
        {
            Kernel = kernel,
            KernelArguments = arguments
        };

        var runTask = Task.Run(async () =>
        {
            string finalText = "";
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(GetTimeout());

            try
            {
                await foreach (var item in agent.InvokeAsync(message, session.Thread, options, timeoutCts.Token))
                {
                    session.Thread = item.Thread;
                    finalText += item.Message.Content ?? "";
                }

                channel.Writer.TryWrite(new AgentStreamEvent.Done(finalText));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                channel.Writer.TryWrite(new AgentStreamEvent.Error($"Foundry/Semantic Kernel agent call timed out after {GetTimeout().TotalSeconds:0} seconds. The deployment may be throttled or stuck in tool-calling."));
            }
            catch (Exception ex)
            {
                channel.Writer.TryWrite(new AgentStreamEvent.Error(NormalizeError(ex)));
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        try
        {
            await foreach (var evt in channel.Reader.ReadAllAsync(ct))
                yield return evt;
        }
        finally
        {
            try { await runTask; }
            catch { /* surfaced via channel events */ }
        }
    }

    public async Task<string> RunAsync(
        ChatCompletionAgent agent,
        string message,
        CancellationToken ct)
    {
        var session = new SkAgentSession();
        string finalText = "";
        await foreach (var evt in RunStreamingAsync(agent, message, session, ct))
        {
            AgentStreamTrace.Record(evt);
            if (evt is AgentStreamEvent.Done done)
                finalText = done.Text;
            else if (evt is AgentStreamEvent.Error err)
                throw new InvalidOperationException(err.Message);
        }
        return finalText;
    }

    internal static KernelArguments BuildArguments()
    {
        var settings = new PromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                autoInvoke: true,
                options: new FunctionChoiceBehaviorOptions
                {
                    AllowConcurrentInvocation = false,
                    AllowParallelCalls = false
                })
        };

        return new KernelArguments(settings);
    }

    private static TimeSpan GetTimeout()
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("AZURE_AI_TIMEOUT_SECONDS"), out var value) && value > 0
            ? value
            : DefaultTimeoutSeconds;

        return TimeSpan.FromSeconds(seconds);
    }

    private static string NormalizeError(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("too_many_requests", StringComparison.OrdinalIgnoreCase)
            ? "Foundry model rate limit hit (HTTP 429). Wait a minute or raise deployment quota."
            : message;
    }

    private sealed class RecordingAutoFunctionInvocationFilter : IAutoFunctionInvocationFilter
    {
        private readonly Action<AgentStreamEvent> _record;

        public RecordingAutoFunctionInvocationFilter(Action<AgentStreamEvent> record)
        {
            _record = record;
        }

        public async Task OnAutoFunctionInvocationAsync(
            AutoFunctionInvocationContext context,
            Func<AutoFunctionInvocationContext, Task> next)
        {
            var toolName = context.Function.Name;
            var callId = string.IsNullOrWhiteSpace(context.ToolCallId)
                ? $"{toolName}_{Guid.NewGuid():N}"
                : context.ToolCallId!;

            Add(new AgentStreamEvent.ToolCall(toolName, callId));

            List<AgentStreamEvent> nestedEvents;
            try
            {
                using var trace = AgentStreamTrace.Push();
                await next(context);
                nestedEvents = trace.Events;
            }
            catch (Exception ex)
            {
                var errorJson = JsonSerializer.Serialize(new { error = true, message = NormalizeError(ex) });
                Add(new AgentStreamEvent.ToolResult(toolName, callId, false, errorJson));
                return;
            }

            foreach (var activity in BuildNestedActivities(callId, nestedEvents))
                Add(activity);

            var resultJson = ResultToString(context.Result);
            Add(new AgentStreamEvent.ToolResult(toolName, callId, IsSuccessfulToolResult(resultJson), resultJson));
        }

        private void Add(AgentStreamEvent evt)
        {
            _record(evt);
            AgentStreamTrace.Record(evt);
        }

        private static IEnumerable<AgentStreamEvent> BuildNestedActivities(
            string parentCallId,
            List<AgentStreamEvent> events)
        {
            foreach (var evt in events)
            {
                switch (evt)
                {
                    case AgentStreamEvent.ToolCall tc:
                    {
                        var id = $"{parentCallId}:{tc.CallId ?? tc.ToolName}";
                        yield return new AgentStreamEvent.Activity(
                            "start", id, parentCallId, "tool", null, tc.ToolName, null, "running",
                            $"Calling {tc.ToolName}");
                        break;
                    }
                    case AgentStreamEvent.ToolResult tr:
                    {
                        var id = $"{parentCallId}:{tr.CallId ?? tr.ToolName}";
                        var (errorType, message) = SummarizeToolResult(tr.ResultJson);
                        yield return new AgentStreamEvent.Activity(
                            "end", id, parentCallId, "tool", null, tr.ToolName, null,
                            tr.Success ? "success" : "failed",
                            tr.Success ? $"{tr.ToolName} completed" : $"{tr.ToolName} failed",
                            Preview(tr.ResultJson), errorType, message);
                        if (tr.Success && string.Equals(tr.ToolName, "ask_clarifying_question", StringComparison.OrdinalIgnoreCase))
                            yield return tr;
                        break;
                    }
                }
            }
        }

        private static string ResultToString(FunctionResult? result)
        {
            if (result is null)
                return "{}";

            var value = result.GetValue<object?>();
            if (value is null)
                return "{}";

            return value is string s ? s : JsonSerializer.Serialize(value);
        }

        private static (string? errorType, string? message) SummarizeToolResult(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(ExtractJsonObject(json));
                var root = doc.RootElement;
                var errorType = root.TryGetProperty("error_type", out var et) ? et.GetString() : null;
                var message = root.TryGetProperty("message", out var msg) ? msg.GetString() :
                    root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.String ? err.GetString() : null;
                return (errorType, message);
            }
            catch { return (null, null); }
        }

        private static bool IsSuccessfulToolResult(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(ExtractJsonObject(json));
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) return true;

                if (TryGetProperty(root, "success", out var successEl) && successEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return successEl.GetBoolean();
                if (TryGetProperty(root, "succeeded", out var succeededEl) && succeededEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    return succeededEl.GetBoolean();
                if (TryGetProperty(root, "needs_replan", out var needsReplanEl) && needsReplanEl.ValueKind == JsonValueKind.True)
                    return false;
                if (TryGetProperty(root, "error_type", out var errorTypeEl) &&
                    errorTypeEl.ValueKind == JsonValueKind.String &&
                    !string.IsNullOrWhiteSpace(errorTypeEl.GetString()))
                    return false;
                if (TryGetProperty(root, "error", out var errorEl) && IsErrorValue(errorEl))
                    return false;
                if (TryGetStatusCode(root, out var statusCode) && statusCode >= 400)
                    return false;

                return true;
            }
            catch
            {
                return !json.Contains("\"error\":true", StringComparison.OrdinalIgnoreCase);
            }
        }

        private static bool IsErrorValue(JsonElement errorEl) => errorEl.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Object => true,
            JsonValueKind.String => !string.IsNullOrWhiteSpace(errorEl.GetString()),
            _ => false
        };

        private static bool TryGetStatusCode(JsonElement root, out int statusCode)
        {
            foreach (var name in new[] { "http_status", "httpStatus", "status", "Status" })
                if (TryGetProperty(root, name, out var statusEl) && statusEl.TryGetInt32(out statusCode))
                    return true;

            statusCode = 0;
            return false;
        }

        private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
        {
            foreach (var property in root.EnumerateObject())
            {
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }

            value = default;
            return false;
        }

        private static string ExtractJsonObject(string text)
        {
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            return start >= 0 && end > start ? text[start..(end + 1)] : text;
        }

        private static string Preview(string value)
        {
            const int max = 900;
            return value.Length <= max ? value : value[..max] + "...";
        }
    }
}
