using System.ComponentModel;
using System.Text.Json;

namespace InfraMapper.Services.Agent.Tools;

public sealed class CriticTools
{
    private readonly PlanStore _planStore;
    private readonly int _revisionCount;

    public CriticTools(PlanStore planStore, int revisionCount)
    {
        _planStore = planStore;
        _revisionCount = revisionCount;
    }

    [Description("Retrieve full details of a plan by its plan_id, including all operations and metadata.")]
    public string GetPlanDetails(
        [Description("The plan_id returned by plan_deployment")] string planId)
    {
        if (!Guid.TryParse(planId, out var guid))
            return JsonSerializer.Serialize(new { error = true, message = "Invalid plan_id format." });

        var data = _planStore.GetPlanData(guid);
        if (data is null)
            return JsonSerializer.Serialize(new { error = true, message = "Plan not found or expired." });

        return data.Value.GetRawText();
    }

    [Description("Record the critic's verdict for a plan. Call this after completing your analysis.")]
    public string RecordVerdict(
        [Description("The plan_id that was reviewed")] string planId,
        [Description("true if the plan passes all checks, false if it needs revision")] bool approved,
        [Description("Detailed feedback: if approved, confirm what passed; if rejected, specify exactly what must change")] string feedback)
    {
        if (!Guid.TryParse(planId, out var guid))
            return JsonSerializer.Serialize(new { error = true, message = "Invalid plan_id format." });

        _planStore.TrySetCriticVerdict(guid, approved, feedback, _revisionCount);

        return JsonSerializer.Serialize(new
        {
            plan_id = planId,
            approved,
            feedback,
            revision_count = _revisionCount,
        });
    }
}
