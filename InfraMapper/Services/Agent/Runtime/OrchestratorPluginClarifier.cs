using System.ComponentModel;
using InfraMapper.Services.Agent.SubAgents;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.Agents;

namespace InfraMapper.Services.Agent.Runtime;

public sealed class OrchestratorPluginClarifier
{
    private readonly SkAgentRunner _runner;
    private readonly ChatCompletionAgent _questioner;
    private readonly string _originatingAgent;

    public OrchestratorPluginClarifier(
        SkAgentRunner runner,
        ChatCompletionAgent questioner,
        string originatingAgent)
    {
        _runner = runner;
        _questioner = questioner;
        _originatingAgent = originatingAgent;
    }

    [KernelFunction("ask_clarifying_question")]
    [Description("Ask the user a targeted clarification question when planning is blocked by ambiguity or a human choice is required.")]
    public async Task<string> AskClarifyingQuestion(
        [Description("Why a user choice is needed")] string context,
        [Description("Recommended default choice if known")] string? recommended_default = null,
        [Description("general, name_correction, scope_confirmation, scope_exclusions, or business_reason")] string category = "general",
        [Description("Destructive or preference scope this answer applies to, if any")] string? confirmation_scope = null,
        [Description("Agent that needs the answer")] string? originating_agent = null,
        CancellationToken cancellationToken = default)
    {
        using var trace = AgentStreamTrace.Push();
        var finalText = await _runner.RunAsync(
            _questioner,
            QuestionerAgent.BuildUserMessage(
                context,
                recommended_default,
                category,
                confirmation_scope,
                string.IsNullOrWhiteSpace(originating_agent) ? _originatingAgent : originating_agent),
            cancellationToken);

        if (QuestionResultExtractor.HasQuestionId(finalText)) return finalText;
        return QuestionResultExtractor.FindLastQuestionResult(trace.Events) ?? finalText;
    }
}
