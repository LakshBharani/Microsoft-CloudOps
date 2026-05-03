using System.ComponentModel;
using System.Text.Json;

namespace InfraMapper.Services.Agent.Tools;

public sealed class QuestionerTools
{
    private readonly QuestionStore _questionStore;
    private readonly string _sessionId;
    private readonly string _originatingAgent;

    public QuestionerTools(QuestionStore questionStore, string sessionId, string originatingAgent)
    {
        _questionStore = questionStore;
        _sessionId = sessionId;
        _originatingAgent = originatingAgent;
    }

    [Description("Create a user-facing clarification question with concrete options.")]
    public string CreateQuestion(
        [Description("Short title for the clarification card")] string title,
        [Description("The question to ask the user")] string prompt,
        [Description("Mutually exclusive options the user can choose from")] QuestionOptionDto[] options,
        [Description("Default option value to use if applicable")] string? defaultValue = null,
        [Description("Whether the UI should include a custom answer option")] bool allowCustom = true,
        [Description("Question category: general, scope_confirmation, scope_exclusions, or business_reason")] string category = "general",
        [Description("Destructive or preference scope this answer applies to, if any")] string? confirmationScope = null,
        [Description("Agent that needs this answer; defaults to the caller")] string? originatingAgent = null)
    {
        var questionData = JsonSerializer.SerializeToElement(new
        {
            title,
            prompt,
            options,
            default_value = defaultValue,
            allow_custom = allowCustom,
            category,
            confirmation_scope = confirmationScope,
            originating_agent = string.IsNullOrWhiteSpace(originatingAgent) ? _originatingAgent : originatingAgent
        }, OrchestratorTools.SnakeCaseOpts);

        var questionId = _questionStore.CreateQuestion(_sessionId, questionData);
        return JsonSerializer.Serialize(new
        {
            question_id = questionId.ToString(),
            title,
            prompt,
            options,
            default_value = defaultValue,
            allow_custom = allowCustom,
            category,
            confirmation_scope = confirmationScope,
            originating_agent = string.IsNullOrWhiteSpace(originatingAgent) ? _originatingAgent : originatingAgent
        }, OrchestratorTools.SnakeCaseOpts);
    }
}

public record QuestionOptionDto(
    [property: Description("Short button label")] string Label,
    [property: Description("Stable value sent back to the agent")] string Value,
    [property: Description("One-sentence tradeoff or implication")] string Description);
