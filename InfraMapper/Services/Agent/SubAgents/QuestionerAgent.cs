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

    public (AnthropicAgent Agent, AgentTool Function) BuildForSession(string sessionId)
    {
        var tools = new QuestionerTools(_questionStore, sessionId);
        var agent = new AnthropicAgent(
            _client,
            AgentRegistry.GetModel("questioner"),
            SystemPrompt,
            [AgentToolFactory.Create(tools.CreateQuestion,
                "create_question",
                "Create a clarification question with options and optional custom answer.")]);

        var function = new AgentTool
        {
            Name = "ask_clarifying_question",
            Description =
                "Ask the user a targeted clarification question when planning is blocked by ambiguity " +
                "or critic feedback requires a human choice. Returns question JSON for the UI.",
            InputSchema = """{"type":"object","properties":{"context":{"type":"string","description":"Why a user choice is needed"},"recommended_default":{"type":"string","description":"Recommended default choice if known"}},"required":["context"]}""",
            Invoke = async (argsJson, ct) =>
            {
                var message = BuildUserMessage(argsJson);
                return await agent.RunAsync(message, ct);
            }
        };

        return (agent, function);
    }

    private static string BuildUserMessage(string? argsJson)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return "Create a clarification question.";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            var context = root.TryGetProperty("context", out var c) ? c.GetString() : null;
            var recommended = root.TryGetProperty("recommended_default", out var r) ? r.GetString() : null;
            return $"Create a clarification question.\nContext:\n{context}\nRecommended default: {recommended ?? "none"}";
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

        After create_question returns, output ONLY its raw JSON response. No other text.
        Do NOT use emojis.
        """;
}
