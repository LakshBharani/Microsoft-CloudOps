using InfraMapper.Services.Agent.Memory;
using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.Tools;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent.SubAgents;

public sealed class ReflectorAgent
{
    private readonly SkAgentFactory _agentFactory;
    private readonly ILessonsStore _lessonsStore;

    public ReflectorAgent(SkAgentFactory agentFactory, ILessonsStore lessonsStore)
    {
        _agentFactory = agentFactory;
        _lessonsStore = lessonsStore;
    }

    public ChatCompletionAgent Build(object? clarificationPlugin = null)
    {
        var tools = new ReflectorTools(_lessonsStore);
        var plugins = new List<(object Plugin, string Name)> { (tools, "reflector") };
        if (clarificationPlugin is not null)
            plugins.Add((clarificationPlugin, "clarification"));

        return _agentFactory.Create(
            "reflector",
            SystemPrompt,
            plugins.ToArray());
    }

    public static string BuildUserMessage(string summary)
    {
        return $"Reflect on this deployment:\n{summary}";
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
