using Anthropic;
using InfraMapper.Services.Agent.AgentFramework;
using InfraMapper.Services.Agent.Memory;
using InfraMapper.Services.Agent.Tools;

namespace InfraMapper.Services.Agent.SubAgents;

/// <summary>
/// Builds the ReflectorAgent (Haiku 4.5) which performs post-deployment audits and records
/// lessons for cross-session memory.
/// Exposed to the Orchestrator as the "reflect_on_deployment" agent-tool.
/// </summary>
public sealed class ReflectorAgent
{
    private readonly AnthropicClient _client;
    private readonly ILessonsStore _lessonsStore;

    public ReflectorAgent(AnthropicClient client, ILessonsStore lessonsStore)
    {
        _client = client;
        _lessonsStore = lessonsStore;
    }

    /// <summary>Builds the ReflectorAgent and its "reflect_on_deployment" AgentTool.</summary>
    public (AnthropicAgent Agent, AgentTool Function) Build(AgentTool? clarificationTool = null)
    {
        var tools = new ReflectorTools(_lessonsStore);
        var agentTools = BuildAgentTools(tools, clarificationTool);

        var agent = new AnthropicAgent(
            _client,
            AgentRegistry.GetModel("reflector"),
            SystemPrompt,
            agentTools);

        var function = new AgentTool
        {
            Name = "reflect_on_deployment",
            Description =
                "Audit a completed deployment and record lessons for cross-session memory. " +
                "Call this after every execute_plan (success or failure) to build institutional knowledge. " +
                "Accepts a summary of what was deployed, what succeeded, and what failed.",
            InputSchema = """{"type":"object","properties":{"summary":{"type":"string","description":"Summary of what was deployed, what succeeded, and what failed"}},"required":["summary"]}""",
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
        if (string.IsNullOrWhiteSpace(argsJson)) return "Reflect on the deployment.";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            var summary = root.TryGetProperty("summary", out var s) ? s.GetString() : null;
            return $"Reflect on this deployment:\n{summary ?? "No summary provided"}";
        }
        catch { return "Reflect on the deployment."; }
    }

    private static IList<AgentTool> BuildAgentTools(ReflectorTools tools, AgentTool? clarificationTool)
    {
        List<AgentTool> toolsList =
        [
            AgentToolFactory.Create(tools.WriteLesson,
                "write_lesson",
                "Write a lesson learned from this deployment to persistent memory."),
        ];
        if (clarificationTool is not null)
            toolsList.Add(clarificationTool);
        return toolsList;
    }

    private const string SystemPrompt = """
        You are InfraMapper Reflector — you audit completed Azure deployments and record lessons
        for future deployment planning sessions.

        When asked to reflect on a deployment, you will receive a deployment summary. Analyze it and:

        1. Identify the intent (what was being deployed).
        2. Identify which Azure resource types were involved.
        3. Determine what failed or caused retries, if anything.
        4. Formulate a specific, actionable recommendation for future deployments of the same type.
           Good recommendations:
             - "Storage account names must be lowercase alphanumeric, max 24 chars; avoid hyphens."
             - "Premium_ZRS not available in eastus; use West US 2 or East US 2."
             - "Always create the resource group before creating resources inside it."
           Bad recommendations:
             - "Make sure the template is correct." (too vague)
             - "The deployment succeeded." (not a lesson)

        5. Call write_lesson with the intent summary, resource types, failure details (if any),
           and the recommendation.

        CRITICAL RULES:
        • You MUST call write_lesson. No exceptions, even for fully successful deployments
          (a lesson about what worked is still valuable).
        • After write_lesson returns, output ONLY its raw JSON result as your final response.
        • If a human answer is required to record a meaningful lesson, call ask_clarifying_question
          and then output ONLY its raw JSON result. Do not ask in prose.
        • Do NOT use emojis.
        """;
}
