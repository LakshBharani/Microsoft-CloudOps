using Anthropic;
using InfraMapper.Services.Agent.AgentFramework;
using InfraMapper.Services.Agent.Tools;

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
    private readonly AnthropicClient _client;
    private readonly PlanStore _planStore;

    public CriticAgent(AnthropicClient client, PlanStore planStore)
    {
        _client = client;
        _planStore = planStore;
    }

    /// <summary>
    /// Builds the CriticAgent + its "critique_plan" AgentTool.
    /// <paramref name="revisionCount"/> is the number of prior revision cycles for this session.
    /// </summary>
    public (AnthropicAgent Agent, AgentTool Function) BuildForSession(int revisionCount = 0, AgentTool? clarificationTool = null)
    {
        var tools = new CriticTools(_planStore, revisionCount);
        var agentTools = BuildAgentTools(tools, clarificationTool);

        var agent = new AnthropicAgent(
            _client,
            AgentRegistry.GetModel("critic"),
            SystemPrompt,
            agentTools);

        var function = new AgentTool
        {
            Name = "critique_plan",
            Description =
                "Validate a deployment plan for correctness, naming rules, dependency ordering, " +
                "region/SKU compatibility, and security. Returns { approved, feedback, plan_id, revision_count }. " +
                "You MUST call this after every plan_deployment before presenting a plan to the user.",
            InputSchema = """{"type":"object","properties":{"plan_id":{"type":"string","description":"The plan_id to critique"}},"required":["plan_id"]}""",
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
        if (string.IsNullOrWhiteSpace(argsJson)) return "Critique the current plan.";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            var planId = root.TryGetProperty("plan_id", out var p) ? p.GetString() : null;
            return $"Critique plan with id: {planId ?? "latest"}";
        }
        catch { return "Critique the current plan."; }
    }

    private static IList<AgentTool> BuildAgentTools(CriticTools tools, AgentTool? clarificationTool)
    {
        List<AgentTool> toolsList =
        [
            AgentToolFactory.Create(tools.GetPlanDetails,
                "get_plan_details",
                "Retrieve the full details of a deployment plan by plan_id."),
            AgentToolFactory.Create(tools.RecordVerdict,
                "record_verdict",
                "Record the critic verdict (approved/rejected) with detailed feedback."),
        ];
        if (clarificationTool is not null)
            toolsList.Add(clarificationTool);
        return toolsList;
    }

    private const string SystemPrompt = """
        You are InfraMapper Critic — a strict Azure deployment plan reviewer. Your job is to catch
        issues BEFORE they reach Azure, saving time and preventing failed deployments.

        When asked to critique a plan, follow this process:

        1. Call get_plan_details to retrieve the full plan. It returns:
           { title, risk_level, operations: [{ action, resource_type, resource_name, resource_group, details }] }

           IMPORTANT: Operations are high-level descriptors, NOT ARM templates.
           The ARM template is constructed by the Executor at deploy time.
           Validate based on resource_type, resource_name, resource_group, and the details field.
           Do NOT reject a plan for lacking ARM template JSON — that is expected and correct.

        2. Assess the plan type:

           DELETE-ONLY PLAN (all operations have action "Delete"):
           Apply only these checks:
           • Risk level must be "High" if a resource group is being deleted, "Medium" otherwise.
           • Each delete operation must explicitly name the resource being deleted (no wildcards).
           • Approve immediately if these pass. Skip naming, dependency, SKU, and security checks.

           CREATE/UPDATE/DEPLOY PLAN (any operation is not Delete):
           Evaluate against ALL of these criteria:

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
           • approved: true ONLY if the plan passes all applicable checks.
           • approved: false if ANY applicable check fails.
           • feedback: if approved, briefly confirm what passed; if rejected, list EVERY specific issue
             that must be fixed, with the resource name and exact correction required.
             If the fix requires a human preference rather than an Azure correctness correction,
             include "requires_user_choice" in the feedback and state the exact choice needed.

        CRITICAL RULES:
        • You MUST call record_verdict before finishing. No exceptions.
        • After record_verdict succeeds, your final response MUST be the exact JSON returned by
          record_verdict. No other text.
        • For delete-only plans: be fast and permissive — only block on the two checks above.
        • For create/update plans: be strict. A plan that might work is not good enough.
        • Accept matching scope-level confirmation evidence in operation details; do not demand
          per-resource typed confirmation when the prior answer covers the same destructive scope.
        • If a human choice is required and no matching confirmation evidence exists, call
          ask_clarifying_question and then output ONLY its raw JSON result. Do not ask in prose.
        • Do NOT use emojis.
        """;
}
