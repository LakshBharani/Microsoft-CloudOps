using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace InfraMapper.Services.Agent.AgentFramework;

public static class AgentToolFactory
{
    private static readonly JsonSerializerOptions ToolJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    public static AgentTool Create(Delegate method, string name, string description)
    {
        var mi = method.Method;
        var parameters = mi.GetParameters();
        var schema = BuildInputSchema(parameters);

        return new AgentTool
        {
            Name = name,
            Description = description,
            InputSchema = schema,
            Invoke = async (argsJson, ct) =>
            {
                var argValues = BuildArgValues(mi, parameters, argsJson, ct);
                var result = mi.Invoke(method.Target, argValues);
                return result switch
                {
                    Task<string> t => await t,
                    Task<object?> t => JsonSerializer.Serialize(await t),
                    Task t => await t.ContinueWith(_ => "{}", ct),
                    string s => s,
                    null => "{}",
                    var other => JsonSerializer.Serialize(other)
                };
            }
        };
    }

    // ─── Schema building ──────────────────────────────────────────────────────

    private static string BuildInputSchema(ParameterInfo[] parameters)
    {
        var props = new StringBuilder();
        var required = new List<string>();
        bool first = true;

        foreach (var p in parameters)
        {
            if (p.ParameterType == typeof(CancellationToken)) continue;

            var schema = BuildPropertySchema(p.ParameterType);
            var desc = p.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "";
            var isNullable = IsNullable(p);

            if (!first) props.Append(',');
            first = false;

            props.Append($"\"{p.Name}\":{schema[..^1]}");
            if (!string.IsNullOrEmpty(desc))
                props.Append($",\"description\":{JsonSerializer.Serialize(desc)}");
            props.Append('}');

            if (!isNullable && !p.HasDefaultValue)
                required.Add(p.Name!);
        }

        var requiredJson = required.Count > 0
            ? ",\"required\":[" + string.Join(",", required.Select(r => $"\"{r}\"")) + "]"
            : "";

        return $"{{\"type\":\"object\",\"properties\":{{{props}}}{requiredJson}}}";
    }

    private static string BuildPropertySchema(Type t)
    {
        var underlying = Nullable.GetUnderlyingType(t) ?? t;
        if (underlying.IsArray)
            return "{\"type\":\"array\",\"items\":" + BuildPropertySchema(underlying.GetElementType()!) + "}";

        if (underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(List<>))
            return "{\"type\":\"array\",\"items\":" + BuildPropertySchema(underlying.GetGenericArguments()[0]) + "}";

        if (underlying.IsClass && underlying != typeof(string) && !underlying.IsGenericType)
        {
            var props = underlying.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            if (props.Length > 0)
            {
                var propertyJson = string.Join(",", props.Select(prop =>
                {
                    var name = JsonNamingPolicy.SnakeCaseLower.ConvertName(prop.Name);
                    return $"\"{name}\":{BuildPropertySchema(prop.PropertyType)}";
                }));
                return "{\"type\":\"object\",\"properties\":{" + propertyJson + "}}";
            }
        }

        var jsonType =
            underlying == typeof(string) ? "string" :
            underlying == typeof(int) || underlying == typeof(long) ? "integer" :
            underlying == typeof(bool) ? "boolean" :
            underlying == typeof(float) || underlying == typeof(double) ? "number" :
            underlying.IsGenericType && underlying.GetGenericTypeDefinition() == typeof(Dictionary<,>) ? "object" :
            "string";

        return "{\"type\":\"" + jsonType + "\"}";
    }

    private static bool IsNullable(ParameterInfo p)
    {
        if (p.HasDefaultValue) return true;
        if (Nullable.GetUnderlyingType(p.ParameterType) != null) return true;
        // Check NullabilityInfoContext for reference-type nullability
        var ctx = new NullabilityInfoContext();
        var info = ctx.Create(p);
        return info.WriteState == NullabilityState.Nullable
            || info.ReadState  == NullabilityState.Nullable;
    }

    // ─── Argument binding ─────────────────────────────────────────────────────

    private static object?[] BuildArgValues(
        MethodInfo mi, ParameterInfo[] parameters, string? argsJson, CancellationToken ct)
    {
        JsonDocument? doc = null;
        if (!string.IsNullOrWhiteSpace(argsJson))
        {
            try { doc = JsonDocument.Parse(argsJson); }
            catch { /* ignore parse errors; fall through to defaults */ }
        }

        using (doc)
        {
            var values = new object?[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                if (p.ParameterType == typeof(CancellationToken))
                {
                    values[i] = ct;
                    continue;
                }

                JsonElement el = default;
                bool found = doc is not null
                    && doc.RootElement.ValueKind == JsonValueKind.Object
                    && doc.RootElement.TryGetProperty(p.Name!, out el);

                if (!found)
                {
                    values[i] = p.HasDefaultValue ? p.DefaultValue : GetDefault(p.ParameterType);
                    continue;
                }

                values[i] = DeserializeElement(el, p.ParameterType);
            }
            return values;
        }
    }

    private static object? DeserializeElement(JsonElement el, Type targetType)
    {
        var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;
        try
        {
            return underlying switch
            {
                _ when underlying == typeof(string)  => el.GetString(),
                _ when underlying == typeof(int)     => el.GetInt32(),
                _ when underlying == typeof(long)    => el.GetInt64(),
                _ when underlying == typeof(bool)    => el.GetBoolean(),
                _ when underlying == typeof(float)   => (float)el.GetDouble(),
                _ when underlying == typeof(double)  => el.GetDouble(),
                _                                    => JsonSerializer.Deserialize(el.GetRawText(), targetType, ToolJsonOptions)
            };
        }
        catch
        {
            return GetDefault(targetType);
        }
    }

    private static object? GetDefault(Type t) =>
        t.IsValueType && Nullable.GetUnderlyingType(t) == null
            ? Activator.CreateInstance(t)
            : null;
}
