using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent.SubAgents;

public sealed class QuestionerAgent
{
    private readonly SkAgentFactory _agentFactory;
    private readonly QuestionStore _questionStore;

    public QuestionerAgent(SkAgentFactory agentFactory, QuestionStore questionStore)
    {
        _agentFactory = agentFactory;
        _questionStore = questionStore;
    }

    public ChatCompletionAgent BuildForSession(string sessionId, string originatingAgent = "orchestrator")
    {
        var tools = new QuestionerTools(_questionStore, sessionId, originatingAgent);
        return _agentFactory.Create(
            "questioner",
            SystemPrompt,
            (tools, "questioner"));
    }

    public static string BuildUserMessage(
        string context,
        string? recommendedDefault,
        string? category,
        string? confirmationScope,
        string originatingAgent)
    {
        return $"""
            Create a clarification question.
            Originating agent: {originatingAgent}
            Category: {category ?? "general"}
            Confirmation scope: {confirmationScope ?? "none"}
            Context:
            {context}
            Recommended default: {recommendedDefault ?? "none"}
            """;
    }

    private const string SystemPrompt = """
        You are InfraMapper Questioner. Create focused user clarification questions for Azure planning.

        Call create_question exactly once. Ask only one question. Provide 2-3 meaningful options.
        The UI will add a Custom option when allow_custom is true.

        Good questions choose between deployment intent, region/SKU tradeoffs, destructive scope,
        or critic feedback requiring human preference. Do not ask for facts discoverable by Azure
        resource reads. Do not ask for subscription ID.

        Pass category to create_question:
        - scope_confirmation for destructive intent or safety confirmation.
        - scope_exclusions for exclusions, resources to keep, or boundaries.
        - business_reason for why a destructive action is intended.
        - name_correction when Azure naming rules make a requested resource name invalid.
        - general for all other clarifications.
        Pass confirmation_scope when the answer should apply to a destructive scope.
        Pass originating_agent exactly as provided in the user message.

        After create_question returns, output ONLY its raw JSON response. No other text.
        Do NOT use emojis.
        """;
}
