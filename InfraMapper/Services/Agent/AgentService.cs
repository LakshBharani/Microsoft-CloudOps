using System.Runtime.CompilerServices;
using System.Text.Json;
using InfraMapper.Models;
using InfraMapper.Models.Agent;
using InfraMapper.Services.Agent.Runtime;
using InfraMapper.Services.Agent.Tools;

namespace InfraMapper.Services.Agent;

public sealed class AgentService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    private readonly ConversationStore _store;
    private readonly PlanStore _planStore;
    private readonly SkAgentRunner _runner;
    private readonly InfraIntentCompiler _intentCompiler;
    private readonly IServiceProvider _services;

    public AgentService(
        ConversationStore store,
        PlanStore planStore,
        SkAgentRunner runner,
        InfraIntentCompiler intentCompiler,
        IServiceProvider services)
    {
        _store = store;
        _planStore = planStore;
        _runner = runner;
        _intentCompiler = intentCompiler;
        _services = services;
    }

    public async Task<AgentChatResponse> ChatAsync(AgentChatRequest request, CancellationToken ct)
    {
        string? reply = null;
        string? sessionId = null;

        await foreach (var evt in StreamAsync(request, ct))
        {
            using var doc = JsonDocument.Parse(evt);
            var root = doc.RootElement;
            if (root.GetProperty("type").GetString() != "reply") continue;

            var data = root.GetProperty("data");
            reply = data.GetProperty("content").GetString();
            sessionId = data.GetProperty("session_id").GetString();
        }

        return new AgentChatResponse { Reply = reply ?? "", SessionId = sessionId ?? request.SessionId ?? "" };
    }

    public async IAsyncEnumerable<string> StreamAsync(
        AgentChatRequest request,
        [EnumeratorCancellation] CancellationToken ct,
        bool autoApprovePlan = true)
    {
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString()
            : request.SessionId;

        ConversationStore.SessionEntry? entry = null;
        string? initError = null;
        try
        {
            entry = await _store.GetOrCreateAsync(sessionId, request.SubscriptionId, ct);
        }
        catch (Exception ex)
        {
            initError = ex.Message;
        }

        if (initError is not null)
        {
            yield return SseEventTranslator.Evt("error", new { message = initError, session_id = sessionId });
            yield break;
        }

        var translator = new SseEventTranslator(sessionId, _planStore);
        var stream = TryCompile(request.Message, request.SubscriptionId, out var compiled, out var compileError)
            ? RunCompiledIntentAsync(sessionId, request.SubscriptionId, compiled!, ct)
            : _runner.RunStreamingAsync(entry!.Agent, BuildAgentMessage(request.Message, request.SubscriptionId, compileError), entry.Session, ct);

        await foreach (var evt in translator.TranslateAsync(stream, ct))
            yield return evt;
    }

    public void ResumeAfterPlanApproval(string sessionId, Guid planId)
    {
        // Prototype mode auto-executes plans. Method remains so existing controller/frontend calls compile.
    }

    private async IAsyncEnumerable<AgentStreamEvent> RunCompiledIntentAsync(
        string sessionId,
        string subscriptionId,
        CompiledInfraIntent compiled,
        [EnumeratorCancellation] CancellationToken ct)
    {
        var tools = ActivatorUtilities.CreateInstance<AzureCrudTools>(_services, sessionId, subscriptionId);

        await foreach (var evt in RunToolAsync(
            "list_resources",
            callId => tools.ListResourcesAsync(subscriptionId, compiled.DesiredState.Scope.FirstOrDefault(), ct)))
            yield return evt;

        var operations = JsonSerializer.SerializeToElement(compiled.Operations.Select(o => new
        {
            action = o.Action,
            resource_type = o.ResourceType,
            resource_name = o.ResourceName,
            resource_group = o.ResourceGroup,
            details = o.Details
        }), AzureCrudTools.JsonOpts);
        using var templateDoc = JsonDocument.Parse(compiled.TemplateJson);
        using var parametersDoc = JsonDocument.Parse("{}");

        string? planResult = null;
        await foreach (var evt in RunToolAsync(
            "create_plan",
            _ => Task.FromResult(tools.CreatePlan(
                "Create Azure infrastructure",
                operations,
                compiled.Operations.Length > 3 ? "High" : "Medium",
                templateDoc.RootElement,
                parametersDoc.RootElement,
                compiled.ResourceGroupName,
                compiled.Location,
                compiled.DeploymentName))))
        {
            if (evt is AgentStreamEvent.ToolResult tr)
                planResult = tr.ResultJson;
            yield return evt;
        }

        if (planResult is null || !AgentResultParser.IsSuccessfulToolResult(planResult))
        {
            yield return new AgentStreamEvent.Done("Plan creation failed.");
            yield break;
        }

        string? deployResult = null;
        await foreach (var evt in RunToolAsync(
            "deploy_arm_template",
            _ => tools.DeployArmTemplateAsync(
                subscriptionId,
                compiled.DeploymentName,
                compiled.TemplateJson,
                "{}",
                compiled.ResourceGroupName,
                compiled.Location,
                "Incremental",
                ct)))
        {
            if (evt is AgentStreamEvent.ToolResult tr)
                deployResult = tr.ResultJson;
            yield return evt;
        }

        await foreach (var evt in RunToolAsync(
            "get_deployment_status",
            _ => tools.GetDeploymentStatusAsync(subscriptionId, compiled.DeploymentName, compiled.ResourceGroupName, ct)))
            yield return evt;

        var success = deployResult is not null && AgentResultParser.IsSuccessfulToolResult(deployResult);
        yield return new AgentStreamEvent.Done(success
            ? $"Created Azure infrastructure successfully. Deployment `{compiled.DeploymentName}` completed."
            : $"Deployment `{compiled.DeploymentName}` failed. Check the tool error above for exact Azure details.");
    }

    private static async IAsyncEnumerable<AgentStreamEvent> RunToolAsync(
        string toolName,
        Func<string, Task<string>> run)
    {
        var callId = $"{toolName}_{Guid.NewGuid():N}";
        yield return new AgentStreamEvent.ToolCall(toolName, callId);
        string result;
        bool success;
        try
        {
            result = await run(callId);
            success = AgentResultParser.IsSuccessfulToolResult(result);
        }
        catch (Exception ex)
        {
            result = AgentResultJson.Serialize(new
            {
                ok = false,
                kind = AgentResultKinds.ToolError,
                error = new { type = "internal", message = ex.Message },
                message = ex.Message
            });
            success = false;
        }

        yield return new AgentStreamEvent.ToolResult(toolName, callId, success, result);
    }

    private string BuildAgentMessage(string rawMessage, string fallbackSubscriptionId, string? compileError)
    {
        var compiledContext = TryCompileIntent(rawMessage, fallbackSubscriptionId);
        if (compiledContext is null)
            return compileError is null
                ? rawMessage
                : $"{rawMessage}\n\nServer compile warning: {compileError}";

        return $"""
            User request:
            {rawMessage}

            Deterministic compile context from server:
            {compiledContext}

            Use this context as the preferred plan/template unless live Azure inspection shows it must change.
            Still follow the required workflow: inspect Azure, create_plan, execute, verify.
            """;
    }

    private string? TryCompileIntent(string rawMessage, string fallbackSubscriptionId)
    {
        if (!TryCompile(rawMessage, fallbackSubscriptionId, out var compiled, out var error))
        {
            return error is null ? null : JsonSerializer.Serialize(new
            {
                compile_warning = error,
                instruction = "If the user JSON is valid enough, infer a plan yourself. If required fields are missing, return a clear failure."
            }, JsonOpts);
        }

        return JsonSerializer.Serialize(new
        {
            deployment_name = compiled!.DeploymentName,
            location = compiled.Location,
            resource_group_name = compiled.ResourceGroupName,
            operations = compiled.Operations.Select(o => new
            {
                action = o.Action,
                resource_type = o.ResourceType,
                resource_name = o.ResourceName,
                resource_group = o.ResourceGroup,
                details = o.Details
            }),
            template_json = JsonSerializer.Deserialize<JsonElement>(compiled.TemplateJson),
            parameters_json = JsonSerializer.Deserialize<JsonElement>("{}"),
            warnings = compiled.Warnings
        }, JsonOpts);
    }

    private bool TryCompile(string rawMessage, string fallbackSubscriptionId, out CompiledInfraIntent? compiled, out string? error)
    {
        compiled = null;
        error = null;
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(rawMessage));
            if (!InfraIntentCompiler.LooksLikeIntent(doc.RootElement)) return false;

            var spec = doc.RootElement.Deserialize<InfraIntentSpec>(JsonOpts);
            if (spec is null) return false;

            compiled = _intentCompiler.Compile(spec, fallbackSubscriptionId);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string ExtractJsonObject(string text)
    {
        var start = text.IndexOf('{');
        if (start < 0) return text;

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];
            if (escaped)
            {
                escaped = false;
                continue;
            }
            if (ch == '\\' && inString)
            {
                escaped = true;
                continue;
            }
            if (ch == '"')
            {
                inString = !inString;
                continue;
            }
            if (inString) continue;

            if (ch == '{') depth++;
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                    return text[start..(i + 1)];
            }
        }

        return text[start..];
    }
}
