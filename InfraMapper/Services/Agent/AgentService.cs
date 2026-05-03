using System.Runtime.CompilerServices;
using System.Text.Json;
using InfraMapper.Models.Agent;
using InfraMapper.Services.Agent.AgentFramework;

namespace InfraMapper.Services.Agent;

public sealed class AgentService
{
    private readonly ConversationStore _store;
    private readonly PlanStore _planStore;

    public AgentService(ConversationStore store, PlanStore planStore)
    {
        _store = store;
        _planStore = planStore;
    }

    public async Task<AgentChatResponse> ChatAsync(AgentChatRequest request, CancellationToken ct)
    {
        string? reply = null;
        string? sessionId = null;

        await foreach (var evt in StreamAsync(request, ct))
        {
            using var doc = JsonDocument.Parse(evt);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() == "reply")
            {
                var data = root.GetProperty("data");
                reply = data.GetProperty("content").GetString();
                sessionId = data.GetProperty("session_id").GetString();
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

        // --- get or create session ---
        ConversationStore.SessionEntry? entry = null;
        string? initError = null;
        try { entry = await _store.GetOrCreateAsync(sessionId, request.SubscriptionId, ct); }
        catch (Exception ex) { initError = ex.Message; }

        if (initError is not null)
        {
            yield return SseEventTranslator.Evt("error", new { message = initError, session_id = sessionId });
            yield break;
        }

        // Inject approval message if the user approved a plan while the stream was closed.
        var pendingApproval = _store.ConsumePendingApproval(sessionId);
        var pendingQuestionAnswer = _store.ConsumePendingQuestionAnswer(sessionId);
        var pending = string.Join("\n", new[] { pendingApproval, pendingQuestionAnswer }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var effectiveMessage = !string.IsNullOrWhiteSpace(pending)
            ? $"{pending}\n{request.Message}"
            : request.Message;

        // --- build stream ---
        IAsyncEnumerable<string>? events = null;
        string? streamError = null;
        try
        {
            var agentStream = entry!.Agent.RunStreamingAsync(effectiveMessage, entry.Session, ct);
            var translator = new SseEventTranslator(sessionId, _planStore, autoApprovePlan);
            events = translator.TranslateAsync(agentStream, ct);
        }
        catch (Exception ex) { streamError = ex.Message; }

        if (streamError is not null)
        {
            yield return SseEventTranslator.Evt("error", new { message = streamError, session_id = sessionId });
            yield break;
        }

        await foreach (var evt in events!.WithCancellation(ct))
            yield return evt;
    }

    /// <summary>
    /// Called by the plan-approve endpoint after the user clicks Approve in the UI.
    /// Stores a resume message in the session StateBag; the next StreamAsync call picks it up.
    /// </summary>
    public void ResumeAfterPlanApproval(string sessionId, Guid planId) =>
        _store.SetPendingApproval(sessionId, planId);

    public void ResumeAfterQuestionAnswer(string sessionId, Guid questionId, string answer) =>
        _store.SetPendingQuestionAnswer(sessionId, questionId, answer);
}
