using System.ComponentModel;
using System.Text.Json;
using InfraMapper.Services.Agent.Memory;

namespace InfraMapper.Services.Agent.Tools;

/// <summary>
/// Tools available to the PlannerAgent.
/// Enforces draft → record_critique → create_plan flow.
/// One instance per ConversationStore session (sessionId is captured at construction time).
/// </summary>
public sealed class PlannerTools
{
    private readonly PlanStore _planStore;
    private readonly ILessonsStore _lessonsStore;
    private readonly string _sessionId;

    public PlannerTools(PlanStore planStore, ILessonsStore lessonsStore, string sessionId)
    {
        _planStore = planStore;
        _lessonsStore = lessonsStore;
        _sessionId = sessionId;
    }

    /// <summary>
    /// STEP 0 (optional but recommended): Retrieve relevant past lessons before drafting.
    /// No LLM call — pure lookup from the persistent lessons file.
    /// </summary>
    [Description("Retrieve relevant lessons from past deployments for the given resource types. " +
                 "Call this BEFORE drafting to avoid repeating known mistakes.")]
    public string GetLessons(
        [Description("Azure resource types to look up lessons for (e.g. ['Microsoft.Storage/storageAccounts'])")] string[] resourceTypes)
    {
        var lessons = _lessonsStore.Query(resourceTypes);
        if (lessons.Count == 0)
            return JsonSerializer.Serialize(new { lessons = Array.Empty<object>(), message = "No lessons recorded for these resource types yet." });

        return JsonSerializer.Serialize(new { lessons });
    }

    /// <summary>
    /// STEP 2 OF 3: The planner MUST call this before create_plan to commit its critique.
    /// Returns guidance that instructs the LLM to proceed to revision.
    /// </summary>
    [Description("REQUIRED STEP 2 OF 3: Record your critique of the ARM template draft. " +
                 "Analyze naming conventions, required dependencies, region/SKU compatibility, " +
                 "security, and missing required properties. You MUST call this before create_plan.")]
    public string RecordCritique(
        [Description("Detailed critique covering naming, dependencies, region/SKU, security, missing properties, and ordering issues")] string analysis)
    {
        // No persistent storage needed in Phase 2; the tool forces the LLM to surface the critique
        // in its chain-of-thought before committing to create_plan.
        return "Critique recorded. Now revise your ARM template to address every issue you identified, " +
               "then call create_plan with the improved version.";
    }

    /// <summary>
    /// STEP 3 OF 3: Registers the plan in PlanStore and returns full plan JSON that the Orchestrator
    /// can parse to emit the 'plan' SSE event to the frontend.
    /// </summary>
    [Description("REQUIRED STEP 3 OF 3: Submit the final revised deployment plan after self-critique. " +
                 "Call this ONLY after record_critique. Returns plan JSON that must be passed back verbatim.")]
    public string CreatePlan(
        [Description("Short descriptive title for this deployment plan")] string title,
        [Description("Complete list of Azure operations to perform")] PlanOperationDto[] operations,
        [Description("Risk level: Low, Medium, or High")] string riskLevel = "Medium",
        [Description("Optional human-readable cost estimate")] string? estimatedCostNote = null)
    {
        var planDataEl = JsonSerializer.SerializeToElement(
            new { title, operations, risk_level = riskLevel, estimated_cost_note = estimatedCostNote },
            OrchestratorTools.SnakeCaseOpts);

        var planId = _planStore.CreatePlan(_sessionId, planDataEl);

        return JsonSerializer.Serialize(new
        {
            plan_id = planId.ToString(),
            status = "awaiting_user_approval",
            title,
            operations,
            risk_level = riskLevel,
            estimated_cost_note = estimatedCostNote,
        }, OrchestratorTools.SnakeCaseOpts);
    }
}
