using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfraMapper.Models;

public sealed class InfraIntentSpec
{
    public string SchemaVersion { get; set; } = "1.0";
    public string Intent { get; set; } = "";
    public IntentScope Scope { get; set; } = new();
    public List<InfraComponentSpec> Components { get; set; } = [];
    public Dictionary<string, JsonElement> Constraints { get; set; } = [];
}

public sealed class IntentScope
{
    public string SubscriptionId { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string Location { get; set; } = "";
}

public sealed class InfraComponentSpec
{
    public string Kind { get; set; } = "";
    public string Name { get; set; } = "";
    public Dictionary<string, JsonElement> Settings { get; set; } = [];

    [JsonExtensionData]
    public Dictionary<string, JsonElement> ExtensionData { get; set; } = [];

    public bool TryGetString(string name, out string value)
    {
        value = "";
        if (TryGetElement(name, out var el) && el.ValueKind == JsonValueKind.String)
        {
            value = el.GetString() ?? "";
            return !string.IsNullOrWhiteSpace(value);
        }
        return false;
    }

    public bool TryGetBool(string name, out bool value)
    {
        value = false;
        if (!TryGetElement(name, out var el) || el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return false;

        value = el.GetBoolean();
        return true;
    }

    public JsonElement? GetObject(string name) =>
        TryGetElement(name, out var el) && el.ValueKind == JsonValueKind.Object ? el : null;

    private bool TryGetElement(string name, out JsonElement value)
    {
        if (Settings.TryGetValue(name, out value)) return true;
        foreach (var kv in ExtensionData)
        {
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                value = kv.Value;
                return true;
            }
        }
        value = default;
        return false;
    }
}
