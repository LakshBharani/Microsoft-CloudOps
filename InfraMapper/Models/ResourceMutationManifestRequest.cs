using System.Text.Json.Serialization;

namespace InfraMapper.Models;

/// <summary>Manifest for an approved generic ARM resource mutation. Must match exactly on apply (hash).</summary>
public class ResourceMutationManifestRequest
{
    public required string ResourceId { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ResourceMutationOperation Operation { get; set; }

    /// <summary>Required for <see cref="ResourceMutationOperation.CreateOrUpdate"/>.</summary>
    public string? Location { get; set; }

    /// <summary>ARM resource properties JSON object. Defaults to <c>{}</c> when null for create/update.</summary>
    public string? PropertiesJson { get; set; }

    public Dictionary<string, string>? Tags { get; set; }

    /// <summary>JSON for <see cref="Azure.ResourceManager.Resources.Models.ResourcesSku"/> (e.g. <c>{"name":"Standard_LRS","tier":"Standard"}</c>).</summary>
    public string? SkuJson { get; set; }

    public string? Kind { get; set; }

    public bool WaitForCompletion { get; set; } = true;
}
