using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace InfraMapper.Services.Agent.AgentFramework;

/// <summary>
/// Translates IAsyncEnumerable&lt;AgentResponseUpdate&gt; from Agent Framework
/// into the newline-delimited JSON SSE event format consumed by the frontend.
///
/// Existing event types (preserved byte-for-byte): tool_call, tool_result, usage, plan, reply, error.
/// New additive events: agent_call, agent_result (emitted when a sub-agent AIFunction is invoked).
///
/// Phase 3: plan events are buffered until the Critic approves them (silent retry UX).
/// The final emitted plan event carries revision_count so the frontend can show a badge.
/// </summary>
public sealed class SseEventTranslator
{
    private readonly string _sessionId;
    private readonly PlanStore _planStore;
    private readonly bool _autoApprovePlan;

    /// <summary>
    /// Sub-agent names registered via AsAIFunction() — tool names matching these
    /// get agent_call / agent_result events in addition to the normal tool_call / tool_result.
    /// </summary>
    public static readonly HashSet<string> SubAgentToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "investigate_infrastructure",
        "plan_deployment",
        "critique_plan",
        "execute_plan",
        "reflect_on_deployment",
    };

    // Tracks iteration counts per sub-agent tool across one streaming run.
    private readonly Dictionary<string, int> _subAgentIterations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (string agentName, int iteration)> _pendingSubAgentCalls = new();

    // Phase 3: plan event buffering for silent retry UX.
    private string? _bufferedPlanResultJson;  // raw result JSON from plan_deployment; null until buffered
    private int _planRevisionCount;           // incremented each time Critic rejects

    public SseEventTranslator(string sessionId, PlanStore planStore, bool autoApprovePlan)
    {
        _sessionId = sessionId;
        _planStore = planStore;
        _autoApprovePlan = autoApprovePlan;
    }

    public async IAsyncEnumerable<string> TranslateAsync(
        IAsyncEnumerable<AgentResponseUpdate> stream,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var callIdToName = new Dictionary<string, string>();
        var textBuilder = new StringBuilder();
        long totalInput = 0, totalOutput = 0;
        string? errorMessage = null;

        await foreach (var update in stream.WithCancellation(ct))
        {
            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case FunctionCallContent fcc:
                    {
                        var toolName = fcc.Name ?? "";
                        if (fcc.CallId is not null)
                            callIdToName[fcc.CallId] = toolName;

                        yield return Evt("tool_call", new { tool = toolName, session_id = _sessionId });

                        if (SubAgentToolNames.Contains(toolName))
                        {
                            _subAgentIterations.TryGetValue(toolName, out var prev);
                            var iter = prev + 1;
                            _subAgentIterations[toolName] = iter;

                            if (fcc.CallId is not null)
                                _pendingSubAgentCalls[fcc.CallId] = (toolName, iter);

                            var model = AgentRegistry.GetModel(ToolNameToAgentName(toolName));
                            yield return Evt("agent_call", new
                            {
                                agent = toolName,
                                model,
                                iteration = iter,
                                parent_tool_call_id = fcc.CallId,
                                session_id = _sessionId
                            });
                        }
                        break;
                    }

                    case FunctionResultContent frc:
                    {
                        var toolName = frc.CallId is not null
                            ? callIdToName.GetValueOrDefault(frc.CallId, "")
                            : "";
                        var resultStr = frc.Result?.ToString() ?? "";
                        var isError = resultStr.Contains("\"error\":true");

                        yield return Evt("tool_result", new { tool = toolName, success = !isError, session_id = _sessionId });

                        if (frc.CallId is not null && _pendingSubAgentCalls.TryGetValue(frc.CallId, out var pending))
                        {
                            _pendingSubAgentCalls.Remove(frc.CallId);
                            yield return Evt("agent_result", new
                            {
                                agent = pending.agentName,
                                success = !isError,
                                iteration = pending.iteration,
                                input_tokens = 0,
                                output_tokens = 0,
                                session_id = _sessionId
                            });
                        }

                        // Buffer plan events from plan_deployment (Phase 2) or create_plan (Phase 0/1 fallback).
                        // The plan SSE event is held until the Critic approves or the stream ends.
                        if ((toolName == "plan_deployment" || toolName == "create_plan") && !isError)
                        {
                            _bufferedPlanResultJson = resultStr;
                        }

                        // Phase 3: Critic result decides whether to emit or discard the buffered plan.
                        if (toolName == "critique_plan" && !isError)
                        {
                            foreach (var evt in HandleCritiqueResult(resultStr))
                                yield return evt;
                        }
                        break;
                    }

                    case TextContent tc:
                        if (!string.IsNullOrEmpty(tc.Text))
                            textBuilder.Append(tc.Text);
                        break;

                    case UsageContent uc:
                        totalInput += uc.Details?.InputTokenCount ?? 0;
                        totalOutput += uc.Details?.OutputTokenCount ?? 0;
                        break;

                    case ErrorContent ec:
                        errorMessage = ec.Message;
                        break;
                }
            }
        }

        if (errorMessage is not null)
        {
            yield return Evt("error", new { message = errorMessage, session_id = _sessionId });
            yield break;
        }

        // If a plan was buffered but never critiqued (Phase 0/1 compatibility or auto-approve path),
        // emit it now at end of stream.
        if (_bufferedPlanResultJson is not null)
        {
            foreach (var evt in EmitBufferedPlan())
                yield return evt;
        }

        if (totalInput > 0 || totalOutput > 0)
            yield return Evt("usage", new
            {
                input_tokens = (int)totalInput,
                output_tokens = (int)totalOutput,
                session_id = _sessionId
            });

        var text = textBuilder.ToString();
        yield return Evt("reply", new
        {
            content = text.Length > 0 ? text : "Done.",
            session_id = _sessionId
        });
    }

    // ─── Plan buffering helpers ──────────────────────────────────────────────

    private IEnumerable<string> HandleCritiqueResult(string critiqueJson)
    {
        bool? approved = null;
        try
        {
            using var doc = JsonDocument.Parse(critiqueJson);
            if (doc.RootElement.TryGetProperty("approved", out var approvedEl))
                approved = approvedEl.GetBoolean();
        }
        catch { /* malformed — treat as approved to avoid blocking */ }

        if (approved == false)
        {
            // Critic rejected: discard the buffered plan, increment revision counter.
            _bufferedPlanResultJson = null;
            _planRevisionCount++;
            yield break;
        }

        // Critic approved (or result unparseable): emit the buffered plan.
        foreach (var evt in EmitBufferedPlan())
            yield return evt;
    }

    private IEnumerable<string> EmitBufferedPlan()
    {
        if (_bufferedPlanResultJson is null) yield break;

        foreach (var evt in BuildPlanEvents(_bufferedPlanResultJson, _planRevisionCount))
            yield return evt;

        _bufferedPlanResultJson = null;
    }

    private IEnumerable<string> BuildPlanEvents(string resultJson, int revisionCount)
    {
        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(resultJson); }
        catch { yield break; }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("plan_id", out var planIdEl) ||
                !Guid.TryParse(planIdEl.GetString(), out var planGuid))
                yield break;

            if (_autoApprovePlan)
            {
                _planStore.TryApprove(planGuid, out _);
                yield return Evt("plan_auto_approved", new { plan_id = planGuid, session_id = _sessionId });
            }
            else
            {
                var ops = root.TryGetProperty("operations", out var opsEl)
                    ? JsonSerializer.Deserialize<object[]>(opsEl.GetRawText())
                    : Array.Empty<object>();

                yield return Evt("plan", new
                {
                    plan_id = planIdEl.GetString(),
                    title = root.TryGetProperty("title", out var t) ? t.GetString() : null,
                    operations = ops,
                    risk_level = root.TryGetProperty("risk_level", out var r) ? r.GetString() : "Medium",
                    estimated_cost_note = root.TryGetProperty("estimated_cost_note", out var e) && e.ValueKind != JsonValueKind.Null
                        ? e.GetString() : null,
                    revision_count = revisionCount,
                    session_id = _sessionId
                });
            }
        }
    }

    /// <summary>Maps sub-agent tool names to AgentRegistry agent names for model lookup.</summary>
    private static string ToolNameToAgentName(string toolName) => toolName switch
    {
        "investigate_infrastructure" => "investigator",
        "plan_deployment"            => "planner",
        "critique_plan"              => "critic",
        "execute_plan"               => "executor",
        "reflect_on_deployment"      => "reflector",
        _                            => toolName
    };

    internal static string Evt(string type, object data) =>
        JsonSerializer.Serialize(new { type, data = JsonSerializer.SerializeToElement(data) });
}
