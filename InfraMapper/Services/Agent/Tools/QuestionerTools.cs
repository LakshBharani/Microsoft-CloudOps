using System.ComponentModel;
using System.Text.Json;
using Microsoft.SemanticKernel;

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

    [KernelFunction("create_question")]
    [Description("Create a user-facing clarification question with concrete options.")]
    public string CreateQuestion(
        [Description("Short title for the clarification card")] string title,
        [Description("The question to ask the user")] string prompt,
        [Description("Mutually exclusive options the user can choose from")] QuestionOptionDto[] options,
        [Description("Default option value to use if applicable")] string? default_value = null,
        [Description("Whether the UI should include a custom answer option")] bool allow_custom = true,
        [Description("Question category: general, scope_confirmation, scope_exclusions, or business_reason")] string category = "general",
        [Description("Destructive or preference scope this answer applies to, if any")] string? confirmation_scope = null,
        [Description("Agent that needs this answer; defaults to the caller")] string? originating_agent = null)
    {
        var questionData = JsonSerializer.SerializeToElement(new
        {
            title,
            prompt,
            options,
            default_value,
            allow_custom,
            category,
            confirmation_scope,
            originating_agent = string.IsNullOrWhiteSpace(originating_agent) ? _originatingAgent : originating_agent
        }, OrchestratorTools.SnakeCaseOpts);

        var questionId = _questionStore.CreateQuestion(_sessionId, questionData);
        return JsonSerializer.Serialize(new
        {
            question_id = questionId.ToString(),
            title,
            prompt,
            options,
            default_value,
            allow_custom,
            category,
            confirmation_scope,
            originating_agent = string.IsNullOrWhiteSpace(originating_agent) ? _originatingAgent : originating_agent
        }, OrchestratorTools.SnakeCaseOpts);
    }
}

public record QuestionOptionDto(
    [property: Description("Short button label")] string Label,
    [property: Description("Stable value sent back to the agent")] string Value,
    [property: Description("One-sentence tradeoff or implication")] string Description);
