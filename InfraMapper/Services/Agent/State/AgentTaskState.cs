using System.Text.Json;

namespace InfraMapper.Services.Agent.State;

public sealed class AgentTaskState
{
    private readonly object _lock = new();
    private readonly List<RequiredComponent> _requiredComponents = new();
    private readonly Dictionary<string, string> _requiredTags = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ClarificationAnswerSnapshot> _answers = new();
    private readonly List<ExecutionFailureSnapshot> _failureHistory = new();

    public string SessionId { get; init; } = "";
    public string SubscriptionId { get; private set; } = "";
    public string OriginalUserMessage { get; private set; } = "";
    public string? OriginalIntentJson { get; private set; }
    public string? ResourceGroup { get; private set; }
    public string? Location { get; private set; }
    public bool StudentSafe { get; private set; }
    public bool NoCompute { get; private set; }
    public string? InvestigationSummary { get; set; }
    public Guid? CandidatePlanId { get; set; }
    public Guid? ApprovedPlanId { get; set; }

    public IReadOnlyList<RequiredComponent> RequiredComponents
    {
        get { lock (_lock) return _requiredComponents.ToArray(); }
    }

    public IReadOnlyDictionary<string, string> RequiredTags
    {
        get { lock (_lock) return new Dictionary<string, string>(_requiredTags, StringComparer.OrdinalIgnoreCase); }
    }

    public IReadOnlyList<ClarificationAnswerSnapshot> Answers
    {
        get { lock (_lock) return _answers.ToArray(); }
    }

    public IReadOnlyList<ExecutionFailureSnapshot> FailureHistory
    {
        get { lock (_lock) return _failureHistory.ToArray(); }
    }

    public bool HasIntent
    {
        get { lock (_lock) return _requiredComponents.Count > 0 || !string.IsNullOrWhiteSpace(OriginalIntentJson); }
    }

    public void Initialize(IntentParseResult parsed, string subscriptionId, string originalUserMessage)
    {
        lock (_lock)
        {
            SubscriptionId = string.IsNullOrWhiteSpace(parsed.SubscriptionId) ? subscriptionId : parsed.SubscriptionId;
            OriginalUserMessage = originalUserMessage ?? "";
            OriginalIntentJson = parsed.OriginalIntentJson;
            ResourceGroup = parsed.ResourceGroup;
            Location = parsed.Location;
            StudentSafe = parsed.StudentSafe;
            NoCompute = parsed.NoCompute;

            _requiredComponents.Clear();
            _requiredComponents.AddRange(parsed.RequiredComponents);

            _requiredTags.Clear();
            foreach (var (k, v) in parsed.RequiredTags)
                _requiredTags[k] = v;

            _answers.Clear();
            _failureHistory.Clear();
            CandidatePlanId = null;
            ApprovedPlanId = null;
        }
    }

    public void AddAnswer(ClarificationAnswerSnapshot answer)
    {
        lock (_lock) _answers.Add(answer);
    }

    public void AddFailure(ExecutionFailureSnapshot failure)
    {
        lock (_lock) _failureHistory.Add(failure);
    }

    public bool HasRecentDuplicateFailure()
    {
        lock (_lock)
        {
            if (_failureHistory.Count < 2) return false;
            var a = _failureHistory[^1];
            var b = _failureHistory[^2];
            return string.Equals(a.ErrorType, b.ErrorType, StringComparison.OrdinalIgnoreCase) &&
                   !string.IsNullOrWhiteSpace(a.TemplateHash) &&
                   string.Equals(a.TemplateHash, b.TemplateHash, StringComparison.Ordinal);
        }
    }
}

public sealed record RequiredComponent(
    string Name,
    string Kind,
    string ResourceTypeHint,
    string? ParentName);

public sealed record ClarificationAnswerSnapshot(
    Guid QuestionId,
    string OriginatingAgent,
    string? Title,
    string? Prompt,
    string? SelectedValue,
    string? SelectedLabel,
    DateTimeOffset AnsweredAt);

public sealed record ExecutionFailureSnapshot(
    string ErrorType,
    string Message,
    string? TemplateHash,
    Guid? PlanId,
    DateTimeOffset Timestamp);

public sealed record IntentParseResult(
    string? OriginalIntentJson,
    string SubscriptionId,
    string? ResourceGroup,
    string? Location,
    bool StudentSafe,
    bool NoCompute,
    IReadOnlyList<RequiredComponent> RequiredComponents,
    IReadOnlyDictionary<string, string> RequiredTags);
