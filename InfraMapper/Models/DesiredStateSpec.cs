using System.Text.Json;
using System.Text.Json.Serialization;

namespace InfraMapper.Models;

public sealed class DesiredStateSpec
{
    public List<DesiredResourceNode> Nodes { get; set; } = [];
    public List<DesiredEdge> Edges { get; set; } = [];
    /// <summary>Only diff resources inside these resource groups. If empty, inferred from nodes.</summary>
    public List<string> Scope { get; set; } = [];
}

public sealed class DesiredResourceNode
{
    public string Name { get; set; } = "";
    public string Type { get; set; } = "";
    public string ResourceGroup { get; set; } = "";
    public string Location { get; set; } = "";
    // Accept either a JSON string or an inline object/array from the desired-state file.
    [JsonConverter(typeof(StringOrObjectConverter))]
    public string? SkuJson { get; set; }
    public string? Kind { get; set; }
    public Dictionary<string, string> Tags { get; set; } = [];
}

/// <summary>Deserializes a JSON value that may be a string literal or an inline object/array into a compact JSON string.</summary>
internal sealed class StringOrObjectConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.StartObject or JsonTokenType.StartArray => JsonDocument.ParseValue(ref reader).RootElement.GetRawText(),
            _ => reader.GetString()
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }
}

public sealed class DesiredEdge
{
    public string SourceName { get; set; } = "";
    public string TargetName { get; set; } = "";
    public string DependencyType { get; set; } = "";
}
