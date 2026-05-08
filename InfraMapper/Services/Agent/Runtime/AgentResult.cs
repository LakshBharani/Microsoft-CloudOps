using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfraMapper.Services.Agent.Runtime;

public sealed record AgentResult<T>(
    bool Ok,
    string Kind,
    T? Data = default,
    AgentError? Error = null,
    string? PlanId = null,
    bool NeedsReplan = false,
    QuestionDto? Question = null,
    string? Severity = null);

public sealed record AgentError(
    string Type,
    string Message,
    string? Code = null,
    int? HttpStatus = null,
    bool Retryable = false);

public sealed record QuestionDto(
    string QuestionId,
    string Title,
    string Prompt,
    object[] Options,
    string? DefaultValue = null,
    bool AllowCustom = true,
    string? Category = null,
    string? ConfirmationScope = null,
    string? OriginatingAgent = null);

public sealed record ParsedAgentResult(
    bool HasEnvelope,
    bool? Ok,
    string? Kind,
    string? PlanId,
    bool NeedsReplan,
    string? Severity,
    string? ErrorType,
    string? ErrorCode,
    string? Message,
    int? HttpStatus,
    JsonElement Root);

public static class AgentResultKinds
{
    public const string PlanCreated = "plan_created";
    public const string PlanRejected = "plan_rejected";
    public const string ClarificationRequired = "clarification_required";
    public const string DeploymentSucceeded = "deployment_succeeded";
    public const string DeploymentFailed = "deployment_failed";
    public const string ToolError = "tool_error";
}

public static class AgentResultJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(object value) => JsonSerializer.Serialize(value, Options);
}

public static class AgentResultParser
{
    public static bool TryParse(string json, out ParsedAgentResult result)
    {
        result = default!;
        try
        {
            using var doc = JsonDocument.Parse(ExtractJsonObject(json));
            var root = doc.RootElement.Clone();
            var hasEnvelope = TryGetProperty(root, "kind", out _) || TryGetProperty(root, "ok", out _);

            bool? ok = null;
            if (TryGetProperty(root, "ok", out var okEl) && okEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ok = okEl.GetBoolean();

            var kind = GetString(root, "kind");
            var planId = GetString(root, "plan_id") ?? GetString(root, "planId");
            var needsReplan = GetBool(root, "needs_replan") || GetBool(root, "needsReplan");
            var severity = GetString(root, "severity");
            var message = GetString(root, "message");
            var errorType = GetString(root, "error_type") ?? GetString(root, "errorType");
            var errorCode = GetString(root, "error_code") ?? GetString(root, "errorCode");
            int? httpStatus = GetInt(root, "http_status") ?? GetInt(root, "httpStatus");

            if (TryGetProperty(root, "error", out var errorEl))
            {
                if (errorEl.ValueKind == JsonValueKind.Object)
                {
                    errorType ??= GetString(errorEl, "type");
                    errorCode ??= GetString(errorEl, "code");
                    message ??= GetString(errorEl, "message");
                    httpStatus ??= GetInt(errorEl, "http_status") ?? GetInt(errorEl, "httpStatus");
                }
                else if (errorEl.ValueKind == JsonValueKind.String)
                {
                    message ??= errorEl.GetString();
                    errorType ??= "error";
                }
                else if (errorEl.ValueKind == JsonValueKind.True)
                {
                    errorType ??= "error";
                }
            }

            if (TryGetProperty(root, "error_detail", out var errorDetailEl) && errorDetailEl.ValueKind == JsonValueKind.Object)
            {
                errorType = GetString(errorDetailEl, "type") ?? errorType;
                errorCode = GetString(errorDetailEl, "code") ?? errorCode;
                message = GetString(errorDetailEl, "message") ?? message;
                httpStatus = GetInt(errorDetailEl, "http_status") ?? GetInt(errorDetailEl, "httpStatus") ?? httpStatus;
            }

            if (TryGetProperty(root, "data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
            {
                planId ??= GetString(dataEl, "plan_id") ?? GetString(dataEl, "planId");
                message ??= GetString(dataEl, "message");
            }

            if (TryGetProperty(root, "question", out var questionEl) && questionEl.ValueKind == JsonValueKind.Object)
                kind ??= AgentResultKinds.ClarificationRequired;
            else if (TryGetProperty(root, "question_id", out _))
                kind ??= AgentResultKinds.ClarificationRequired;
            else if (!string.IsNullOrWhiteSpace(planId))
                kind ??= AgentResultKinds.PlanCreated;

            if (TryGetProperty(root, "approved", out var approvedEl) && approvedEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                var approved = approvedEl.GetBoolean();
                severity ??= approved ? "approved" : null;
            }

            result = new ParsedAgentResult(
                hasEnvelope,
                ok,
                kind,
                planId,
                needsReplan,
                severity,
                errorType,
                errorCode,
                message,
                httpStatus,
                root);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsSuccessfulToolResult(string json)
    {
        if (!TryParse(json, out var parsed))
            return !json.Contains("\"error\":true", StringComparison.OrdinalIgnoreCase);

        if (parsed.Ok is bool ok)
            return ok;
        if (parsed.NeedsReplan)
            return false;
        if (!string.IsNullOrWhiteSpace(parsed.ErrorType))
            return false;
        if (parsed.HttpStatus is >= 400)
            return false;
        if (TryGetProperty(parsed.Root, "success", out var successEl) && successEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return successEl.GetBoolean();
        if (TryGetProperty(parsed.Root, "succeeded", out var succeededEl) && succeededEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return succeededEl.GetBoolean();
        if (TryGetProperty(parsed.Root, "error", out var errorEl) && IsErrorValue(errorEl))
            return false;

        return true;
    }

    public static string ExtractJsonObject(string text)
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

    public static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            value = default;
            return false;
        }

        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    public static string? GetString(JsonElement obj, string name)
    {
        if (!TryGetProperty(obj, name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static bool GetBool(JsonElement obj, string name) =>
        TryGetProperty(obj, name, out var value) && value.ValueKind == JsonValueKind.True;

    private static int? GetInt(JsonElement obj, string name) =>
        TryGetProperty(obj, name, out var value) && value.TryGetInt32(out var intValue) ? intValue : null;

    private static bool IsErrorValue(JsonElement errorEl) => errorEl.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.Object => true,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(errorEl.GetString()),
        _ => false
    };
}
