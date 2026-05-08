using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class SkAgentRunner
{
    private const int DefaultTimeoutSeconds = 90;
    private const int InfraAgentTimeoutSeconds = 300;

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

        var timeout = GetTimeout(agent.Name);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);

        var watchdog = timeoutCts.Token.Register(() =>
        {
            Console.WriteLine($"[SkRunner] watchdog_fire agent={agent.Name} timeout={timeout.TotalSeconds}s");
            channel.Writer.TryWrite(new AgentStreamEvent.Error(
                $"OpenAI/Semantic Kernel agent call ({agent.Name ?? "agent"}) timed out after {timeout.TotalSeconds:0} seconds. The deployment may be throttled or stuck in tool-calling."));
            channel.Writer.TryComplete();
        });

        var runTask = Task.Run(async () =>
        {
            string finalText = "";
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            Console.WriteLine($"[SkRunner] invoke_start agent={agent.Name} timeout={timeout.TotalSeconds}s");

            try
            {
                var chunkCount = 0;
                await foreach (var item in agent.InvokeAsync(message, session.Thread, options, timeoutCts.Token))
                {
                    session.Thread = item.Thread;
                    finalText += item.Message.Content ?? "";
                    chunkCount++;
                    if (chunkCount == 1 || chunkCount % 10 == 0)
                        Console.WriteLine($"[SkRunner] agent={agent.Name} chunks={chunkCount} elapsed={stopwatch.Elapsed.TotalSeconds:0.0}s");
                }

                Console.WriteLine($"[SkRunner] invoke_done agent={agent.Name} chunks={chunkCount} elapsed={stopwatch.Elapsed.TotalSeconds:0.0}s text_len={finalText.Length}");
                channel.Writer.TryWrite(new AgentStreamEvent.Done(finalText));
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                Console.WriteLine($"[SkRunner] timeout agent={agent.Name} elapsed={stopwatch.Elapsed.TotalSeconds:0.0}s");
                channel.Writer.TryWrite(new AgentStreamEvent.Error($"OpenAI/Semantic Kernel agent call ({agent.Name ?? "agent"}) timed out after {timeout.TotalSeconds:0} seconds. The deployment may be throttled or stuck in tool-calling."));
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[SkRunner] error agent={agent.Name} elapsed={stopwatch.Elapsed.TotalSeconds:0.0}s {ex.GetType().Name}: {ex.Message}");
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
            watchdog.Dispose();
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
        var settings = new AzureOpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(
                autoInvoke: true,
                options: new FunctionChoiceBehaviorOptions
                {
                    AllowConcurrentInvocation = false
                })
        };

        return new KernelArguments(settings);
    }

    private static TimeSpan GetTimeout(string? agentName)
    {
        if (!string.IsNullOrWhiteSpace(agentName))
        {
            var perAgentVar = $"INFRAMAPPER_{agentName.ToUpperInvariant()}_TIMEOUT_SECONDS";
            if (int.TryParse(Environment.GetEnvironmentVariable(perAgentVar), out var perAgentValue) && perAgentValue > 0)
                return TimeSpan.FromSeconds(perAgentValue);
        }

        if (int.TryParse(Environment.GetEnvironmentVariable("AZURE_AI_TIMEOUT_SECONDS"), out var globalValue) && globalValue > 0)
            return TimeSpan.FromSeconds(globalValue);

        var fallback = string.Equals(agentName, "infra_agent", StringComparison.OrdinalIgnoreCase)
            ? InfraAgentTimeoutSeconds
            : DefaultTimeoutSeconds;
        return TimeSpan.FromSeconds(fallback);
    }

    private static string NormalizeError(Exception ex)
    {
        var message = ex.Message;
        return message.Contains("429", StringComparison.OrdinalIgnoreCase) ||
               message.Contains("too_many_requests", StringComparison.OrdinalIgnoreCase)
            ? "OpenAI model rate limit hit (HTTP 429). Wait a minute or raise model quota."
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

            Console.WriteLine($"[SkRunner] tool_start agent={context.Function.PluginName ?? "unknown"} tool={toolName} call_id={callId}");
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
                Console.WriteLine($"[SkRunner] tool_error tool={toolName} call_id={callId} result={Preview(errorJson)}");
                Add(new AgentStreamEvent.ToolResult(toolName, callId, false, errorJson));
                return;
            }

            foreach (var activity in BuildNestedActivities(callId, nestedEvents))
                Add(activity);

            var resultJson = ResultToString(context.Result);
            var success = IsSuccessfulToolResult(resultJson);
            var (errorType, message) = SummarizeToolResult(resultJson);
            Console.WriteLine($"[SkRunner] tool_done tool={toolName} call_id={callId} success={success} error_type={errorType ?? ""} message={message ?? ""} result_preview={Preview(resultJson)}");
            Add(new AgentStreamEvent.ToolResult(toolName, callId, success, resultJson));
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
            if (AgentResultParser.TryParse(json, out var parsed) &&
                (!string.IsNullOrWhiteSpace(parsed.ErrorType) || !string.IsNullOrWhiteSpace(parsed.Message)))
                return (parsed.ErrorType, parsed.Message);

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
            return AgentResultParser.IsSuccessfulToolResult(json);
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
            if (start < 0) return text;

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var i = start; i < text.Length; i++)
            {
                var ch = text[i];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (ch == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }
                if (ch == '"')
                {
                    inString = !inString;
                    continue;
                }
                if (inString) continue;

                if (ch == '{') depth++;
                else if (ch == '}')
                {
                    depth--;
                    if (depth == 0)
                        return text[start..(i + 1)];
                }
            }

            return text[start..];
        }

        private static string Preview(string value)
        {
            const int max = 900;
            return value.Length <= max ? value : value[..max] + "...";
        }
    }
}
