using System.Text.Json.Serialization;

namespace InfraMapper.Models;

public class ResourceMutationManifestRequest
{
    public required string ResourceId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ResourceMutationOperation Operation { get; set; }

    public string? Location { get; set; }

    public string? PropertiesJson { get; set; }

    public Dictionary<string, string>? Tags { get; set; }

    public string? SkuJson { get; set; }

    public string? Kind { get; set; }

    public bool WaitForCompletion { get; set; } = true;
}
