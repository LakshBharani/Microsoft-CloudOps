using Anthropic;
using InfraMapper.Services.Agent.AgentFramework;
using InfraMapper.Services.Agent.Tools;

namespace InfraMapper.Services.Agent.SubAgents;

public sealed class QuestionerAgent
{
    private readonly AnthropicClient _client;
    private readonly QuestionStore _questionStore;

    public QuestionerAgent(AnthropicClient client, QuestionStore questionStore)
    {
        _client = client;
        _questionStore = questionStore;
    }

    public (AnthropicAgent Agent, AgentTool Function) BuildForSession(string sessionId, string originatingAgent = "orchestrator")
    {
        var tools = new QuestionerTools(_questionStore, sessionId, originatingAgent);
        var agent = new AnthropicAgent(
            _client,
            AgentRegistry.GetModel("questioner"),
            SystemPrompt,
            [AgentToolFactory.Create(tools.CreateQuestion,
                "create_question",
                "Create a clarification question with options and optional custom answer.")]);

        return (agent, BuildFunction(agent, originatingAgent));
    }

    public AgentTool BuildFunctionForSession(string sessionId, string originatingAgent)
    {
        var (agent, function) = BuildForSession(sessionId, originatingAgent);
        return function;
    }

    private static AgentTool BuildFunction(AnthropicAgent agent, string originatingAgent)
    {
        return new AgentTool
        {
            Name = "ask_clarifying_question",
            Description =
                "Ask the user a targeted clarification question when planning is blocked by ambiguity " +
                "or critic feedback requires a human choice. Returns question JSON for the UI.",
            InputSchema = """{"type":"object","properties":{"context":{"type":"string","description":"Why a user choice is needed"},"recommended_default":{"type":"string","description":"Recommended default choice if known"},"category":{"type":"string","description":"general, name_correction, scope_confirmation, scope_exclusions, or business_reason"},"confirmation_scope":{"type":"string","description":"Destructive or preference scope this answer applies to, if any"},"originating_agent":{"type":"string","description":"Agent that needs the answer"}},"required":["context"]}""",
            Invoke = async (argsJson, ct) =>
            {
                var message = BuildUserMessage(argsJson, originatingAgent);
                return await agent.RunAsync(message, ct);
            }
        };
    }

    private static string BuildUserMessage(string? argsJson, string originatingAgent)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return "Create a clarification question.";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            var context = root.TryGetProperty("context", out var c) ? c.GetString() : null;
            var recommended = root.TryGetProperty("recommended_default", out var r) ? r.GetString() : null;
            var category = root.TryGetProperty("category", out var cat) ? cat.GetString() : "general";
            var scope = root.TryGetProperty("confirmation_scope", out var s) ? s.GetString() : null;
            var origin = root.TryGetProperty("originating_agent", out var o) ? o.GetString() : originatingAgent;
            return $"""
                Create a clarification question.
                Originating agent: {origin}
                Category: {category ?? "general"}
                Confirmation scope: {scope ?? "none"}
                Context:
                {context}
                Recommended default: {recommended ?? "none"}
                """;
        }
        catch { return "Create a clarification question."; }
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
