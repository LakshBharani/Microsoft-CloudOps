using Anthropic;
using InfraMapper.Services.Agent.Tools;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace InfraMapper.Services.Agent.SubAgents;

/// <summary>
/// Builds the CriticAgent (Sonnet 4.6) which validates deployment plans before user approval.
/// Exposed to the Orchestrator as the "critique_plan" agent-tool.
///
/// The Critic checks: naming conventions, required dependencies, region/SKU compatibility,
/// security posture, and risk level. Returns approved:true/false with actionable feedback.
/// </summary>
public sealed class CriticAgent
{
    private readonly IAnthropicClient _client;
    private readonly PlanStore _planStore;

    public CriticAgent(IAnthropicClient client, PlanStore planStore)
    {
        _client = client;
        _planStore = planStore;
    }

    /// <summary>
    /// Builds the CriticAgent AIAgent + its "critique_plan" AIFunction.
    /// <paramref name="revisionCount"/> is the number of prior revision cycles for this session.
    /// </summary>
    public (AIAgent Agent, AIFunction Function) BuildForSession(int revisionCount = 0)
    {
        var tools = new CriticTools(_planStore, revisionCount);
        var aiTools = BuildAiTools(tools);

        var agent = _client.AsAIAgent(
            model: AgentRegistry.GetModel("critic"),
            instructions: SystemPrompt,
            name: "InfraMapperCritic",
            description: "Reviews deployment plans for correctness, security, and policy compliance.",
            tools: aiTools);

        var function = agent.AsAIFunction(new AIFunctionFactoryOptions
        {
            Name = "critique_plan",
            Description =
                "Validate a deployment plan for correctness, naming rules, dependency ordering, " +
                "region/SKU compatibility, and security. Returns { approved, feedback, plan_id, revision_count }. " +
                "You MUST call this after every plan_deployment before presenting a plan to the user.",
        });

        return (agent, function);
    }

    private static IList<AITool> BuildAiTools(CriticTools tools)
    {
        var opts = OrchestratorTools.SnakeCaseOpts;
        return
        [
            AIFunctionFactory.Create(tools.GetPlanDetails,
                new AIFunctionFactoryOptions { Name = "get_plan_details", SerializerOptions = opts }),
            AIFunctionFactory.Create(tools.RecordVerdict,
                new AIFunctionFactoryOptions { Name = "record_verdict", SerializerOptions = opts }),
        ];
    }

    private const string SystemPrompt = """
        You are InfraMapper Critic — a strict Azure deployment plan reviewer. Your job is to catch
        issues BEFORE they reach Azure, saving time and preventing failed deployments.

        When asked to critique a plan, follow this process:

        1. Call get_plan_details to retrieve the full plan.

        2. Evaluate the plan against ALL of these criteria:

           NAMING RULES
           • Storage accounts: 3–24 chars, lowercase letters and numbers only, globally unique.
           • Key Vaults: 3–24 chars, alphanumeric and hyphens, must start with letter.
           • Resource groups: 1–90 chars, alphanumeric, underscores, periods, hyphens.
           • VNets/Subnets: 2–64 chars, alphanumeric, periods, hyphens, underscores.
           • VMs: 1–15 chars (Windows), 1–64 chars (Linux).

           DEPENDENCY ORDERING
           • Resource Group must exist before any resource inside it.
           • VNet must exist before Subnet.
           • NSG must exist before associating it with a Subnet or NIC.
           • Public IP must exist before NIC that references it.
           • NIC must exist before VM that uses it.
           • Key Vault must exist before anything that references its secrets.

           REGION / SKU COMPATIBILITY
           • Premium_ZRS storage is only available in specific regions (West US 2, East US 2, West Europe, etc.).
           • Ultra Disk is not available in all zones/regions.
           • Flag if the plan uses a SKU that is known to have regional restrictions.

           SECURITY POSTURE
           • Storage accounts should have HTTPS-only access and TLS 1.2 minimum.
           • NSGs should deny all inbound traffic by default; only allow specific ports.
           • Key Vaults should have soft-delete and purge protection enabled.
           • VMs should not expose RDP/SSH directly to the internet (use Bastion or private IP).

           RISK LEVEL ACCURACY
           • Low: read-only or purely additive, no production resources.
           • Medium: new resources, no existing resources modified.
           • High: existing resource modifications, deletions, or production workloads.

        3. Call record_verdict with:
           • approved: true ONLY if the plan passes all checks.
           • approved: false if ANY check fails.
           • feedback: if approved, briefly confirm what passed; if rejected, list EVERY specific issue
             that must be fixed, with the resource name and exact correction required.

        CRITICAL RULES:
        • You MUST call record_verdict before finishing. No exceptions.
        • After record_verdict succeeds, your final response MUST be the exact JSON returned by
          record_verdict. No other text.
        • Be strict. A plan that might work is not good enough — the plan must definitely work.
        """;
}
