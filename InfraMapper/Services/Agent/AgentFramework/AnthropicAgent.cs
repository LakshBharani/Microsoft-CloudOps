using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Anthropic;
using Anthropic.Models.Messages;

namespace InfraMapper.Services.Agent.AgentFramework;

/// <summary>
/// An agent that runs the standard Anthropic tool-use loop, emitting <see cref="AgentStreamEvent"/>s.
/// Uses the non-streaming Messages API internally so tool dispatch is simple and reliable.
/// The SSE streaming to the browser is handled by <see cref="SseEventTranslator"/>.
/// </summary>
public sealed class AnthropicAgent
{
    private const int MaxTokens = 8192;
    private const int MaxIterations = 20;  // Guard against infinite loops

    private readonly AnthropicClient _client;
    private readonly string _model;
    private readonly string _systemPrompt;
    private readonly IList<AgentTool> _tools;

    public AnthropicAgent(
        AnthropicClient client,
        string model,
        string systemPrompt,
        IList<AgentTool> tools)
    {
        _client = client;
        _model = model;
        _systemPrompt = systemPrompt;
        _tools = tools;
    }

    /// <summary>
    /// Runs the agent in a streaming fashion, yielding <see cref="AgentStreamEvent"/>s.
    /// History is read from and written back to <paramref name="session"/>.
    /// </summary>
    public async IAsyncEnumerable<AgentStreamEvent> RunStreamingAsync(
        string message,
        AnthropicAgentSession session,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // Append the user message to history
        session.History.Add(new MessageParam
        {
            Role = Role.User,
            Content = message,
        });

        var apiTools = BuildApiTools();
        int iterations = 0;

        while (iterations < MaxIterations)
        {
            iterations++;
            Message response;
            string? callError = null;
            try
            {
                response = await _client.Messages.Create(new MessageCreateParams
                {
                    Model = _model,
                    MaxTokens = MaxTokens,
                    System = _systemPrompt,
                    Messages = session.History,
                    Tools = apiTools,
                }, ct);
            }
            catch (Exception ex)
            {
                callError = ex.Message;
                response = null!; // unused, but required by compiler
            }
            if (callError is not null)
            {
                yield return new AgentStreamEvent.Error(callError);
                yield break;
            }

            // Accumulate text and tool-use blocks from the response
            var textBuilder = new StringBuilder();
            var toolCalls = new List<(string Id, string Name, string InputJson)>();

            foreach (var block in response.Content)
            {
                if (block.TryPickText(out var tb))
                {
                    textBuilder.Append(tb.Text);
                }
                else if (block.TryPickToolUse(out var tub))
                {
                    var inputJson = tub.Input is not null
                        ? JsonSerializer.Serialize(tub.Input)
                        : "{}";
                    toolCalls.Add((tub.ID, tub.Name, inputJson));
                    yield return new AgentStreamEvent.ToolCall(tub.Name, tub.ID);
                }
            }

            var stopReason = response.StopReason.Value();

            if (stopReason != StopReason.ToolUse || toolCalls.Count == 0)
            {
                // Emit usage then done
                yield return new AgentStreamEvent.Usage(
                    (int)response.Usage.InputTokens,
                    (int)response.Usage.OutputTokens);
                yield return new AgentStreamEvent.Done(textBuilder.ToString());
                yield break;
            }

            // Append the assistant turn (with tool-use blocks) to history
            // We need to reconstruct the assistant message from the raw response content blocks
            var assistantContentBlocks = BuildAssistantContentBlocks(response.Content);
            session.History.Add(new MessageParam
            {
                Role = Role.Assistant,
                Content = assistantContentBlocks,
            });

            // Execute all tool calls and collect results
            var toolResultBlocks = new List<ContentBlockParam>();
            foreach (var (id, name, inputJson) in toolCalls)
            {
                var tool = _tools.FirstOrDefault(t =>
                    string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));

                string resultJson;
                bool success = true;
                List<AgentStreamEvent> nestedEvents = [];
                try
                {
                    if (tool is null)
                    {
                        resultJson = JsonSerializer.Serialize(new { error = true, message = $"Unknown tool: {name}" });
                        success = false;
                    }
                    else
                    {
                        using var trace = AgentActivityTrace.Push();
                        resultJson = await tool.Invoke(inputJson, ct);
                        nestedEvents = trace.Events;
                        success = IsSuccessfulToolResult(resultJson);
                    }
                }
                catch (Exception ex)
                {
                    resultJson = JsonSerializer.Serialize(new { error = true, message = ex.Message });
                    success = false;
                }

                foreach (var activity in BuildNestedActivities(id, nestedEvents))
                    yield return activity;

                yield return new AgentStreamEvent.ToolResult(name, id, success, resultJson);

                toolResultBlocks.Add(new ToolResultBlockParam(id)
                {
                    Content = new ToolResultBlockParamContent(resultJson, null),
                    IsError = !success,
                });
            }

            // Append tool results to history as a user message
            session.History.Add(new MessageParam
            {
                Role = Role.User,
                Content = toolResultBlocks,
            });
        }

        yield return new AgentStreamEvent.Error("Agent loop exceeded maximum iterations.");
    }

    /// <summary>
    /// Runs the agent without an external session (creates an internal one).
    /// Returns only the final text. Used for sub-agents invoked as tools.
    /// </summary>
    public async Task<string> RunAsync(string message, CancellationToken ct)
    {
        var session = new AnthropicAgentSession();
        string finalText = "";
        await foreach (var evt in RunStreamingAsync(message, session, ct))
        {
            AgentActivityTrace.Record(evt);
            if (evt is AgentStreamEvent.Done done)
                finalText = done.Text;
            else if (evt is AgentStreamEvent.Error err)
                throw new InvalidOperationException(err.Message);
        }
        return finalText;
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
        {
            if (TryGetProperty(root, name, out var statusEl) && statusEl.TryGetInt32(out statusCode))
                return true;
        }

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
        return start >= 0 && end > start
            ? text[start..(end + 1)]
            : text;
    }

    private static string Preview(string value)
    {
        const int max = 900;
        return value.Length <= max ? value : value[..max] + "…";
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private IReadOnlyList<ToolUnion> BuildApiTools()
    {
        if (_tools.Count == 0) return [];

        return _tools.Select(t =>
        {
            var inputSchemaParam = BuildInputSchema(t.InputSchema);
            return (ToolUnion)new Tool
            {
                Name = t.Name,
                Description = t.Description,
                InputSchema = inputSchemaParam,
            };
        }).ToList();
    }

    private static InputSchema BuildInputSchema(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson))
        {
            // Empty schema: accepts any object (or no params)
            return new InputSchema
            {
                Properties = new Dictionary<string, JsonElement>(),
                Required = [],
            };
        }

        try
        {
            using var doc = JsonDocument.Parse(schemaJson);
            var root = doc.RootElement;

            var props = new Dictionary<string, JsonElement>();
            if (root.TryGetProperty("properties", out var propsEl)
                && propsEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in propsEl.EnumerateObject())
                    props[kv.Name] = kv.Value.Clone();
            }

            var required = new List<string>();
            if (root.TryGetProperty("required", out var reqEl)
                && reqEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in reqEl.EnumerateArray())
                    if (item.GetString() is string s)
                        required.Add(s);
            }

            return new InputSchema { Properties = props, Required = required };
        }
        catch
        {
            return new InputSchema
            {
                Properties = new Dictionary<string, JsonElement>(),
                Required = [],
            };
        }
    }

    /// <summary>
    /// Rebuilds the assistant's content block list for re-injection into history.
    /// We need ToolUseBlockParam entries so the history is valid for the next API call.
    /// </summary>
    private static MessageParamContent BuildAssistantContentBlocks(
        IReadOnlyList<ContentBlock> responseContent)
    {
        var blocks = new List<ContentBlockParam>();
        foreach (var block in responseContent)
        {
            if (block.TryPickText(out var tb))
            {
                blocks.Add(new TextBlockParam { Text = tb.Text ?? "" });
            }
            else if (block.TryPickToolUse(out var tub))
            {
                blocks.Add(new ToolUseBlockParam
                {
                    ID = tub.ID,
                    Name = tub.Name,
                    Input = tub.Input ?? new Dictionary<string, JsonElement>(),
                });
            }
        }

        return blocks.Count > 0
            ? new MessageParamContent(blocks, null)
            : new MessageParamContent("", null);
    }
}
