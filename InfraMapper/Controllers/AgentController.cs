using InfraMapper.Models.Agent;
using InfraMapper.Services.Agent;
using Microsoft.AspNetCore.Mvc;

namespace InfraMapper.Controllers;

[ApiController]
[Route("api/agent")]
public sealed class AgentController : ControllerBase
{
    private readonly AgentService _agentService;
    private readonly PlanStore _planStore;
    private readonly QuestionStore _questionStore;

    public AgentController(AgentService agentService, PlanStore planStore, QuestionStore questionStore)
    {
        _agentService = agentService;
        _planStore = planStore;
        _questionStore = questionStore;
    }

    [HttpPost("chat")]
    [ProducesResponseType(typeof(AgentChatResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult<AgentChatResponse>> Chat(
        [FromBody] AgentChatRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message))
            return BadRequest(new { error = "Message is required." });
        if (string.IsNullOrWhiteSpace(request.SubscriptionId))
            return BadRequest(new { error = "SubscriptionId is required." });

        var response = await _agentService.ChatAsync(request, cancellationToken);
        return Ok(response);
    }

    [HttpPost("stream")]
    public async Task StreamChat([FromBody] AgentChatRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Message) || string.IsNullOrWhiteSpace(request.SubscriptionId))
        {
            Response.StatusCode = 400;
            return;
        }

        Response.Headers.Append("Content-Type", "text/event-stream");
        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");

        await foreach (var evt in _agentService.StreamAsync(request, cancellationToken))
        {
            await Response.WriteAsync($"data: {evt}\n\n", cancellationToken);
            await Response.Body.FlushAsync(cancellationToken);
        }
    }

    [HttpPost("plan/{planId}/approve")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult ApprovePlan(Guid planId, [FromQuery] string sessionId)
    {
        if (!_planStore.TryApprove(planId, out var error))
            return BadRequest(new { error });

        _agentService.ResumeAfterPlanApproval(sessionId, planId);
        return Ok(new { approved = true });
    }

    [HttpPost("plan/{planId}/reject")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult RejectPlan(Guid planId)
    {
        if (!_planStore.TryReject(planId, out var error))
            return BadRequest(new { error });

        return Ok(new { rejected = true });
    }

    [HttpPost("question/{questionId}/answer")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult AnswerQuestion(Guid questionId, [FromQuery] string sessionId, [FromBody] QuestionAnswerRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Answer))
            return BadRequest(new { error = "Answer is required." });
        if (!_questionStore.TryAnswer(questionId, request.Answer, out var error))
            return BadRequest(new { error });

        _agentService.ResumeAfterQuestionAnswer(sessionId, questionId, request.Answer);
        return Ok(new { answered = true });
    }
}

public sealed class QuestionAnswerRequest
{
    public required string Answer { get; set; }
}
