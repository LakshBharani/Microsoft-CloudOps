using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.CompilerServices;
using InfraMapper.Models.Agent;

namespace InfraMapper.Services.Agent;

public sealed class AgentService
{
    private const int ContextTrimThreshold = 140_000;
    private const int HistoryKeepTail = 12;

    private static string BuildSystemPrompt(string subscriptionId) => $"""
        You are InfraMapper Agent, an AI assistant that manages Azure infrastructure.

        The user's Azure subscription ID is: {subscriptionId}
        Use this subscription ID for all operations unless the user explicitly specifies a different one.
        Do NOT ask the user for their subscription ID.

        Rules:
        - READ tools (get_infrastructure_graph, get_resource, get_deployment_status): call freely without asking.
        - WRITE tools (deploy_arm_template, apply_resource_mutation): you MUST call create_plan first.
          After create_plan returns a plan_id, inform the user the plan is ready for review.
          Do NOT call write tools until you receive confirmation the plan was approved.
          Once approved, pass the plan_id to the write tool.
        - When generating ARM templates, include all required resources and dependencies. Produce valid ARM template JSON.
        - Be concise. After completing an operation, give a brief summary of what was done.

        Formatting rules:
        - Do NOT use emojis.
        - When writing a markdown table, always put a blank line before and after it, and put each row on its own line.
        - Use bullet points or numbered lists rather than inline-concatenated items.
        """;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly AgentTools _tools;
    private readonly ConversationStore _store;
    private readonly PlanStore _planStore;
    private readonly ConversationStore _conversationStore;

    public AgentService(
        IHttpClientFactory httpClientFactory,
        AgentTools tools,
        ConversationStore store,
        PlanStore planStore)
    {
        _httpClientFactory = httpClientFactory;
        _tools = tools;
        _store = store;
        _planStore = planStore;
        _conversationStore = store;
    }

    public async Task<AgentChatResponse> ChatAsync(AgentChatRequest request, CancellationToken ct)
    {
        string? reply = null;
        string? sessionId = null;

        await foreach (var evt in StreamAsync(request, ct))
        {
            var doc = JsonDocument.Parse(evt);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "reply")
            {
                reply = doc.RootElement.GetProperty("content").GetString();
                sessionId = doc.RootElement.GetProperty("session_id").GetString();
            }
        }

        return new AgentChatResponse { Reply = reply ?? "", SessionId = sessionId ?? request.SessionId ?? "" };
    }

    public async IAsyncEnumerable<string> StreamAsync(
        AgentChatRequest request,
        [EnumeratorCancellation] CancellationToken ct,
        bool autoApprovePlan = false)
    {
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString()
            : request.SessionId;
        var history = _store.GetOrCreate(sessionId);
        var anthropic = new AnthropicApiClient(_httpClientFactory.CreateClient("anthropic"));

        history.Add(MessageParam.User(request.Message));

        var apiRequest = new MessagesRequest
        {
            System = BuildSystemPrompt(request.SubscriptionId),
            Messages = history,
            Tools = _tools.Definitions.ToList()
        };

        int lastInputTokens = 0;

        while (true)
        {
            TrimHistoryIfNeeded(history, lastInputTokens);

            MessagesResponse response;
            string? sendError = null;
            try { response = await anthropic.SendAsync(apiRequest, ct); }
            catch (Exception ex) { sendError = ex.Message; response = null!; }

            if (sendError is not null)
            {
                yield return Evt("error", new { message = sendError, session_id = sessionId });
                yield break;
            }

            lastInputTokens = response.Usage?.InputTokens ?? 0;

            if (response.Usage is not null)
                yield return Evt("usage", new
                {
                    input_tokens = response.Usage.InputTokens,
                    output_tokens = response.Usage.OutputTokens,
                    session_id = sessionId
                });

            var assistantBlocks = new JsonArray();
            foreach (var block in response.Content)
                assistantBlocks.Add(block.ToNode());
            history.Add(MessageParam.Assistant(assistantBlocks));

            if (response.StopReason == "tool_use")
            {
                var toolResultBlocks = new JsonArray();
                foreach (var block in response.Content.Where(b => b.Type == "tool_use"))
                {
                    yield return Evt("tool_call", new { tool = block.Name, session_id = sessionId });

                    var result = await _tools.ExecuteAsync(block.Name!, block.Input, request.SubscriptionId, sessionId, ct);

                    // Emit plan event when create_plan is called
                    if (block.Name == "create_plan")
                    {
                        var planResult = JsonDocument.Parse(result).RootElement;
                        if (planResult.TryGetProperty("plan_id", out var planIdEl) &&
                            Guid.TryParse(planIdEl.GetString(), out var planGuid))
                        {
                            if (autoApprovePlan)
                            {
                                _planStore.TryApprove(planGuid, out _);
                                yield return Evt("plan_auto_approved", new { plan_id = planGuid, session_id = sessionId });
                            }
                            else
                            {
                                yield return Evt("plan", new
                                {
                                    plan_id = planIdEl.GetString(),
                                    title = block.Input.Str("title"),
                                    operations = block.Input.TryGetProperty("operations", out var ops)
                                        ? JsonSerializer.Deserialize<object[]>(ops.GetRawText())
                                        : Array.Empty<object>(),
                                    risk_level = block.Input.Str("risk_level") ?? "Medium",
                                    estimated_cost_note = block.Input.Str("estimated_cost_note"),
                                    session_id = sessionId
                                });
                            }
                        }
                    }

                    var isError = result.Contains("\"error\":true");
                    yield return Evt("tool_result", new { tool = block.Name, success = !isError, session_id = sessionId });

                    toolResultBlocks.Add(new JsonObject
                    {
                        ["type"] = "tool_result",
                        ["tool_use_id"] = block.Id,
                        ["content"] = result
                    });
                }
                history.Add(MessageParam.UserWithBlocks(toolResultBlocks));
                continue;
            }

            var text = string.Concat(response.Content.Where(b => b.Type == "text").Select(b => b.Text));
            yield return Evt("reply", new
            {
                content = text.Length > 0 ? text : $"Stopped: {response.StopReason}",
                session_id = sessionId
            });
            yield break;
        }
    }

    public void ResumeAfterPlanApproval(string sessionId, Guid planId)
    {
        var history = _conversationStore.GetOrCreate(sessionId);
        history.Add(MessageParam.User($"The plan with id {planId} has been approved. Please proceed with execution."));
    }

    private static void TrimHistoryIfNeeded(List<MessageParam> history, int lastInputTokens)
    {
        if (lastInputTokens < ContextTrimThreshold || history.Count <= HistoryKeepTail) return;

        var tail = history.TakeLast(HistoryKeepTail).ToList();
        history.Clear();
        history.Add(MessageParam.User("[Context compacted: earlier conversation history was truncated to fit context window.]"));
        history.AddRange(tail);
    }

    private static string Evt(string type, object data) =>
        JsonSerializer.Serialize(new { type, data = JsonSerializer.SerializeToElement(data) },
            AnthropicApiClient.JsonOpts);
}
