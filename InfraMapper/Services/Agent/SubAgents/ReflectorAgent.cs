using Anthropic;
using InfraMapper.Services.Agent.Memory;
using InfraMapper.Services.Agent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace InfraMapper.Services.Agent.SubAgents;

/// <summary>
/// Builds the ReflectorAgent (Haiku 4.5) which performs post-deployment audits and records
/// lessons for cross-session memory.
/// Exposed to the Orchestrator as the "reflect_on_deployment" agent-tool.
/// </summary>
public sealed class ReflectorAgent
{
    private readonly IAnthropicClient _client;
    private readonly ILessonsStore _lessonsStore;

    public ReflectorAgent(IAnthropicClient client, ILessonsStore lessonsStore)
    {
        _client = client;
        _lessonsStore = lessonsStore;
    }

    /// <summary>Builds the ReflectorAgent and its "reflect_on_deployment" AIFunction.</summary>
    public (AIAgent Agent, AIFunction Function) Build()
    {
        var tools = new ReflectorTools(_lessonsStore);
        var aiTools = BuildAiTools(tools);

        var agent = _client.AsAIAgent(
            model: AgentRegistry.GetModel("reflector"),
            instructions: SystemPrompt,
            name: "InfraMapperReflector",
            description: "Audits completed deployments and records lessons for future use.",
            tools: aiTools);

        var function = agent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = "reflect_on_deployment",
            Description =
                "Audit a completed deployment and record lessons for cross-session memory. " +
                "Call this after every execute_plan (success or failure) to build institutional knowledge. " +
                "Accepts a summary of what was deployed, what succeeded, and what failed.",
        });

        return (agent, function);
    }

    private static IList<AITool> BuildAiTools(ReflectorTools tools)
    {
        return
        [
            AIFunctionFactory.Create(tools.WriteLesson,
                new AIFunctionFactoryOptions
                {
                    Name = "write_lesson",
                    SerializerOptions = OrchestratorTools.SnakeCaseOpts,
                }),
        ];
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
             ✓ "Storage account names must be lowercase alphanumeric, max 24 chars; avoid hyphens."
             ✓ "Premium_ZRS not available in eastus; use West US 2 or East US 2."
             ✓ "Always create the resource group before creating resources inside it."
           Bad recommendations:
             ✗ "Make sure the template is correct." (too vague)
             ✗ "The deployment succeeded." (not a lesson)

        5. Call write_lesson with the intent summary, resource types, failure details (if any),
           and the recommendation.

        CRITICAL RULES:
        • You MUST call write_lesson. No exceptions, even for fully successful deployments
          (a lesson about what worked is still valuable).
        • After write_lesson returns, output ONLY its raw JSON result as your final response.
        """;
}
