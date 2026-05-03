using Anthropic;
using InfraMapper.Services.Agent.AgentFramework;
using InfraMapper.Services.Agent.Memory;
using InfraMapper.Services.Agent.Tools;

namespace InfraMapper.Services.Agent.SubAgents;

/// <summary>
/// Builds the PlannerAgent (Sonnet 4.6) which enforces a draft → self-critique → revise loop
/// before registering any deployment plan.
///
/// The Orchestrator invokes it as an agent-tool named "plan_deployment".
/// </summary>
public sealed class PlannerAgent
{
    private readonly AnthropicClient _client;
    private readonly PlanStore _planStore;
    private readonly ILessonsStore _lessonsStore;

    public PlannerAgent(AnthropicClient client, PlanStore planStore, ILessonsStore lessonsStore)
    {
        _client = client;
        _planStore = planStore;
        _lessonsStore = lessonsStore;
    }

    /// <summary>
    /// Builds a PlannerAgent + its "plan_deployment" AgentTool for the given session.
    /// Call once per ConversationStore session during session initialisation.
    /// </summary>
    public (AnthropicAgent Agent, AgentTool Function) BuildForSession(string sessionId, AgentTool? clarificationTool = null)
    {
        var tools = new PlannerTools(_planStore, _lessonsStore, sessionId);
        var agentTools = BuildAgentTools(tools, clarificationTool);

        var agent = new AnthropicAgent(
            _client,
            AgentRegistry.GetModel("planner"),
            SystemPrompt,
            agentTools);

        var function = new AgentTool
        {
            Name = "plan_deployment",
            Description =
                "Generate a complete ARM deployment plan for the given intent. " +
                "The planner drafts an ARM template, self-critiques it, revises it, then registers the plan. " +
                "Returns plan JSON including plan_id, title, operations, and risk_level. " +
                "Always call this instead of create_plan when you need to deploy Azure resources.",
            InputSchema = """{"type":"object","properties":{"intent":{"type":"string","description":"What the user wants to deploy or change"},"investigator_summary":{"type":"string","description":"Optional summary from the investigator agent"}},"required":["intent"]}""",
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
        if (string.IsNullOrWhiteSpace(argsJson)) return "Plan the deployment.";
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(argsJson);
            var root = doc.RootElement;
            var intent  = root.TryGetProperty("intent", out var i) ? i.GetString() : null;
            var summary = root.TryGetProperty("investigator_summary", out var s) ? s.GetString() : null;
            var msg = $"Plan deployment: {intent ?? "as described"}";
            if (!string.IsNullOrWhiteSpace(summary))
                msg += $"\n\nInvestigator summary:\n{summary}";
            return msg;
        }
        catch { return "Plan the deployment."; }
    }

    // ─── Internal tool wiring ────────────────────────────────────────────────

    private static IList<AgentTool> BuildAgentTools(PlannerTools tools, AgentTool? clarificationTool)
    {
        List<AgentTool> toolsList =
        [
            // Step 0 (optional): look up past lessons before drafting.
            AgentToolFactory.Create(tools.GetLessons,
                "get_lessons",
                "Retrieve past deployment lessons for specific Azure resource types."),
            // Step 2: forced critique commit before create_plan.
            AgentToolFactory.Create(tools.RecordCritique,
                "record_critique",
                "Record a self-critique of the current ARM template draft."),
            // Step 3: final plan creation.
            AgentToolFactory.Create(tools.CreatePlan,
                "create_plan",
                "Create and register the final deployment plan after self-critique."),
        ];
        if (clarificationTool is not null)
            toolsList.Add(clarificationTool);
        return toolsList;
    }

    // ─── System prompt ───────────────────────────────────────────────────────

    private const string SystemPrompt = """
        You are InfraMapper Planner — an Azure ARM template expert. Your job is to produce
        deployment-ready plans that pass validation on the first attempt.

        When asked to plan a deployment, follow this process:

        ═══ STEP 0 — CHECK LESSONS (recommended) ═══
        Call get_lessons with the relevant Azure resource types to retrieve past lessons.
        If lessons exist, apply their recommendations in your draft (e.g. avoid known bad SKUs,
        use correct naming patterns). This step is optional but strongly recommended.

        ═══ STEP 1 — DRAFT ═══
        In your response text, produce a complete ARM template draft. For every resource you plan to
        create, list:
          • Azure resource type (e.g. Microsoft.Storage/storageAccounts)
          • Resource name (follow Azure naming rules: 3-24 chars lowercase alphanumeric for storage, etc.)
          • Location / region
          • Required SKU / tier
          • All required properties (API version, kind, sku, properties block)
          • Dependencies (which resources must exist first)

        ═══ STEP 2 — CRITIQUE ═══
        Call record_critique with a thorough analysis of your draft. Evaluate each of:
          1. Naming: Does each name comply with Azure naming rules for its resource type?
          2. Dependencies: Is every dependency resource included and ordered correctly?
          3. Region/SKU: Is the chosen SKU available in the target region? (e.g. Premium_ZRS only in select regions)
          4. Security: Are NSGs present? Are storage accounts using HTTPS-only + TLS 1.2?
          5. Required properties: Are all mandatory fields populated (kind, sku.name, sku.tier, API version)?
          6. Circular dependencies or ordering issues.
        Be specific about what needs to change.

        ═══ STEP 3 — REVISE AND SUBMIT ═══
        Address every issue identified in Step 2, then call create_plan with:
          • title: a short descriptive name for this deployment
          • operations: the complete revised list (action, resource_type, resource_name, resource_group, details)
          • risk_level: Low (read-only or additive), Medium (new resources), High (destructive or production)
          • estimated_cost_note: a brief cost note if relevant

        If the Orchestrator provides a question_answer, treat it as a hard planning constraint and
        reflect it in operation details.

        CRITICAL RULES:
          • You MUST call record_critique BEFORE calling create_plan — no exceptions.
          • After create_plan returns, output ONLY its raw JSON as your final response. No other text.
          • If a human choice is required, call ask_clarifying_question and then output ONLY
            its raw JSON result. Do not continue planning until the user answers.
          • Do NOT ask user-facing questions in prose.
          • Include prior clarification evidence in destructive operation details so Critic can
            verify scope-level confirmation without asking again.
          • Never skip steps or collapse them into one.
          • Always include ALL dependency resources in the operations list, even if the user didn't mention them.
          • Do NOT use emojis.
        """;
}
