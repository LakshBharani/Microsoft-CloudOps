using System.Runtime.CompilerServices;
using System.Text.Json;
using InfraMapper.Models.Agent;
using InfraMapper.Services.Agent.Runtime;

namespace InfraMapper.Services.Agent;

public sealed class AgentService
{
    private readonly ConversationStore _store;
    private readonly PlanStore _planStore;
    private readonly IAgentRunner _runner;
    private readonly InfraIntentValidator _intentValidator;
    private readonly QuestionStore _questionStore;

    public AgentService(
        ConversationStore store,
        PlanStore planStore,
        IAgentRunner runner,
        InfraIntentValidator intentValidator,
        QuestionStore questionStore)
    {
        _store = store;
        _planStore = planStore;
        _runner = runner;
        _intentValidator = intentValidator;
        _questionStore = questionStore;
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

    public async Task RunTerminalChatAsync(AgentChatRequest request, CancellationToken ct)
    {
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString()
            : request.SessionId;
        var message = request.Message;

        Console.WriteLine();
        Console.WriteLine("InfraMapper CLI agent");
        Console.WriteLine($"Session: {sessionId}");
        Console.WriteLine("Status: started from website. Keep this terminal open for plans, questions, and apply progress.");
        Console.WriteLine();

        for (var turn = 0; turn < 6; turn++)
        {
            var requestForTurn = new AgentChatRequest
            {
                Message = message,
                SubscriptionId = request.SubscriptionId,
                SessionId = sessionId
            };
            var answeredQuestion = false;

            await foreach (var evt in StreamAsync(requestForTurn, ct))
                answeredQuestion = PrintTerminalEvent(evt, sessionId) || answeredQuestion;

            if (!answeredQuestion)
                break;

            message = "continue with clarification answer";
            Console.WriteLine();
            Console.WriteLine("Continuing with your clarification...");
            Console.WriteLine();
        }

        Console.WriteLine();
        Console.WriteLine("InfraMapper CLI agent finished.");
        Console.WriteLine();
    }

    public async IAsyncEnumerable<string> StreamAsync(
        AgentChatRequest request,
        [EnumeratorCancellation] CancellationToken ct,
        bool autoApprovePlan = true)
    {
        var sessionId = string.IsNullOrWhiteSpace(request.SessionId)
            ? Guid.NewGuid().ToString()
            : request.SessionId;
        _store.Touch(sessionId);

        var pendingClarification = _store.ConsumePendingClarification(sessionId);
        var effectiveMessage = string.IsNullOrWhiteSpace(pendingClarification)
            ? request.Message
            : $"{pendingClarification}\n\nContinue with the clarified infrastructure task.";

        var translator = new SseEventTranslator(sessionId, _planStore);
        IAsyncEnumerable<AgentStreamEvent> stream;
        var validation = _intentValidator.Validate(effectiveMessage, request.SubscriptionId);
        if (validation.Error is not null)
        {
            stream = SingleError(validation.Error);
        }
        else
        {
            var agentMessage = validation.IsValid
                ? BuildIntentAgentMessage(validation, request.SubscriptionId, sessionId)
                : BuildAgentMessage(effectiveMessage, request.SubscriptionId, sessionId, compileError: null);
            stream = _runner.RunStreamingAsync(agentMessage, sessionId, request.SubscriptionId, ct);
        }

        await foreach (var evt in translator.TranslateAsync(stream, ct))
            yield return evt;
    }

    public void ResumeAfterPlanApproval(string sessionId, Guid planId)
    {
        _store.Touch(sessionId);
    }

    public void ResumeAfterQuestionAnswer(string sessionId, Guid questionId, string answer)
    {
        _store.SetPendingClarification(sessionId, _questionStore.FormatAnswer(questionId, answer));
    }

    private bool PrintTerminalEvent(string evt, string sessionId)
    {
        using var doc = JsonDocument.Parse(evt);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        var data = root.GetProperty("data");

        switch (type)
        {
            case "tool_call":
                Console.WriteLine($"tool: {GetString(data, "tool")} ...");
                break;
            case "tool_result":
                Console.WriteLine($"tool: {GetString(data, "tool")} {(GetBool(data, "success") ? "ok" : "failed")}");
                break;
            case "activity_end":
                if (string.Equals(GetString(data, "status"), "failed", StringComparison.OrdinalIgnoreCase))
                {
                    var tool = GetString(data, "tool");
                    var message = GetString(data, "message");
                    var detail = GetString(data, "detail_preview");
                    var errorType = GetString(data, "error_type");
                    var prefix = string.IsNullOrWhiteSpace(tool) ? "failure" : $"{tool} failure";

                    if (!string.IsNullOrWhiteSpace(message))
                        Console.WriteLine($"{prefix}: {message}");
                    else if (!string.IsNullOrWhiteSpace(detail))
                        Console.WriteLine($"{prefix}: {detail}");
                    else if (!string.IsNullOrWhiteSpace(errorType))
                        Console.WriteLine($"{prefix}: {errorType}");
                }
                break;
            case "plan":
                PrintPlan(data);
                break;
            case "question":
                return PromptForQuestion(data, sessionId);
            case "usage":
                Console.WriteLine($"tokens: {GetInt(data, "input_tokens"):N0} in / {GetInt(data, "output_tokens"):N0} out");
                break;
            case "reply":
                var content = GetString(data, "content");
                if (!string.IsNullOrWhiteSpace(content))
                {
                    Console.WriteLine();
                    Console.WriteLine(content.Trim());
                }
                break;
            case "error":
                Console.WriteLine($"error: {GetString(data, "message")}");
                break;
        }

        return false;
    }

    private static void PrintPlan(JsonElement data)
    {
        Console.WriteLine();
        Console.WriteLine($"plan: {GetString(data, "title")}");
        Console.WriteLine($"risk: {GetString(data, "risk_level")}");

        if (data.TryGetProperty("operations", out var ops) && ops.ValueKind == JsonValueKind.Array)
        {
            var i = 1;
            foreach (var op in ops.EnumerateArray())
            {
                var action = GetString(op, "action");
                var type = GetString(op, "resource_type");
                var name = GetString(op, "resource_name");
                var group = GetString(op, "resource_group");
                Console.WriteLine($"{i}. {action} {type} {name}{(string.IsNullOrWhiteSpace(group) ? "" : $" in {group}")}");
                i++;
            }
        }

        Console.WriteLine("status: auto-approved for prototype apply");
        Console.WriteLine();
    }

    private bool PromptForQuestion(JsonElement data, string sessionId)
    {
        var questionIdText = GetString(data, "question_id");
        if (!Guid.TryParse(questionIdText, out var questionId))
            return false;

        Console.WriteLine();
        Console.WriteLine($"question: {GetString(data, "title")}");
        Console.WriteLine(GetString(data, "prompt"));

        if (data.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            var i = 1;
            foreach (var option in options.EnumerateArray())
            {
                var label = GetString(option, "label");
                var value = GetString(option, "value");
                var description = GetString(option, "description");
                Console.WriteLine($"{i}. {label}{(string.IsNullOrWhiteSpace(value) ? "" : $" [{value}]")}{(string.IsNullOrWhiteSpace(description) ? "" : $" - {description}")}");
                i++;
            }
        }

        var defaultValue = GetString(data, "default_value");
        Console.Write(string.IsNullOrWhiteSpace(defaultValue)
            ? "Answer: "
            : $"Answer [{defaultValue}]: ");
        var answer = Console.ReadLine();
        if (string.IsNullOrWhiteSpace(answer))
            answer = defaultValue;

        if (string.IsNullOrWhiteSpace(answer))
        {
            Console.WriteLine("No answer entered; waiting for the next run.");
            return false;
        }

        if (!_questionStore.TryAnswer(questionId, answer, out var error))
        {
            Console.WriteLine($"question answer failed: {error}");
            return false;
        }

        ResumeAfterQuestionAnswer(sessionId, questionId, answer);
        Console.WriteLine("answer saved");
        return true;
    }

    private static string GetString(JsonElement root, string name)
    {
        if (!TryGetProperty(root, name, out var value))
            return "";

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? "",
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => ""
        };
    }

    private static bool GetBool(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int GetInt(JsonElement root, string name) =>
        TryGetProperty(root, name, out var value) && value.TryGetInt32(out var result) ? result : 0;

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private string BuildAgentMessage(string rawMessage, string subscriptionId, string sessionId, string? compileError)
    {
        var validation = _intentValidator.Validate(rawMessage, subscriptionId);
        if (!validation.IsValid)
            return compileError is null ? rawMessage : $"{rawMessage}\n\nServer warning: {compileError}";

        return $"""
            {ConversationStore.BuildSystemPrompt(subscriptionId, sessionId)}

            User request:
            {rawMessage}

            {BuildIntentAgentMessage(validation, subscriptionId, sessionId)}
            """;
    }

    internal static string BuildIntentAgentMessage(InfraIntentValidationResult validation) =>
        BuildIntentAgentMessage(validation, validation.Spec?.Scope.SubscriptionId ?? "sub", "test-session");

    internal static string BuildIntentAgentMessage(InfraIntentValidationResult validation, string subscriptionId, string sessionId)
    {
        var warnings = validation.Warnings.Count == 0
            ? "None."
            : string.Join("\n", validation.Warnings.Select(w => $"- {w}"));

        return $"""
            {ConversationStore.BuildSystemPrompt(subscriptionId, sessionId)}

            Generate an Azure deployment plan and execute it for this InfraIntentSpec.
            Use noCompute and studentSafe constraints exactly as written.
            Prefer deploy_arm_template for multiple related resources.
            Use create_or_update_resource only for simple single-resource CRUD.
            Do not create resources outside the requested subscription, resource group, location, or component scope.
            Generate ARM resource definitions dynamically with type, apiVersion, name, sku, kind, properties, dependsOn, and tags.
            Always inspect Azure first, create_plan before writes, execute after auto-approval, then verify with get_deployment_status or list_resources.
            Before planning resources in a resource group, call list_resource_groups; if the target resource group does not exist, include a "Create Microsoft.Resources/resourceGroups <name>" operation as step 1 of the plan and emit a subscription-scope ARM template that creates the resource group and a nested deployment for its child resources.
            For ARM deployment plans, include template_json and parameters_json in create_plan, then pass the same template object to deploy_arm_template.
            Never call create_plan with empty arguments. It requires a non-empty operations array, and ARM plans must include the same ARM template object you will pass to deploy_arm_template.
            Never call deploy_arm_template without a template object containing at least contentVersion and resources.
            If Azure validation/deployment returns an error, explain it and repair the ARM once or twice if possible.
            You must finish with a normal assistant text response after tool calls complete. Do not return an empty final message.
            Final response must include:
            - plan title or deployment name
            - resources created or updated
            - verification tool used and result
            - any Azure error if deployment or verification failed

            Server pre-check warnings:
            {warnings}

            Normalized InfraIntentSpec:
            ```json
            {validation.NormalizedJson}
            ```
            """;
    }

    private static async IAsyncEnumerable<AgentStreamEvent> SingleError(string message)
    {
        yield return new AgentStreamEvent.Error(message);
        await Task.CompletedTask;
    }
}
