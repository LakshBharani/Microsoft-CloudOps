using System.Net;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.Resources;
using Azure.ResourceManager.Resources.Models;
using InfraMapper.Models;

namespace InfraMapper.Services;

public sealed class ArmGenericResourceService : IArmGenericResourceService
{
    private readonly ArmClient _armClient;

    public ArmGenericResourceService(ArmClient armClient)
    {
        _armClient = armClient;
    }

    public async Task<GenericResourceOperationResult> GetAsync(string resourceId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            return Fail((int)HttpStatusCode.BadRequest, null, "ResourceId is required.");

        ResourceIdentifier rid;
        try
        {
            rid = ResourceIdentifier.Parse(resourceId);
        }
        catch (FormatException ex)
        {
            return Fail((int)HttpStatusCode.BadRequest, null, $"Invalid resource id: {ex.Message}");
        }

        try
        {
            var collection = _armClient.GetGenericResources();
            var response = await collection.GetAsync(rid, cancellationToken).ConfigureAwait(false);
            return MapSuccess(response.Value);
        }
        catch (RequestFailedException ex)
        {
            return Fail(ex.Status, ex.ErrorCode, ex.Message);
        }
    }

    public async Task<GenericResourceOperationResult> CreateOrUpdateAsync(
        string resourceId,
        string location,
        string? propertiesJson,
        IReadOnlyDictionary<string, string>? tags,
        string? skuJson,
        string? kind,
        bool waitForCompletion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            return Fail((int)HttpStatusCode.BadRequest, null, "ResourceId is required.");

        if (string.IsNullOrWhiteSpace(location))
            return Fail((int)HttpStatusCode.BadRequest, null, "Location is required for CreateOrUpdate.");

        ResourceIdentifier rid;
        try
        {
            rid = ResourceIdentifier.Parse(resourceId);
        }
        catch (FormatException ex)
        {
            return Fail((int)HttpStatusCode.BadRequest, null, $"Invalid resource id: {ex.Message}");
        }

        var props = string.IsNullOrWhiteSpace(propertiesJson) ? "{}" : propertiesJson!;
        try
        {
            using var _ = JsonDocument.Parse(props);
        }
        catch (JsonException ex)
        {
            return Fail((int)HttpStatusCode.BadRequest, null, $"PropertiesJson is not valid JSON: {ex.Message}");
        }

        var data = new GenericResourceData(new AzureLocation(location))
        {
            Properties = BinaryData.FromString(props)
        };

        if (!string.IsNullOrWhiteSpace(kind))
            data.Kind = kind;

        if (tags != null)
        {
            foreach (var kv in tags)
                data.Tags[kv.Key] = kv.Value;
        }

        if (!string.IsNullOrWhiteSpace(skuJson))
        {
            if (!TryParseResourcesSku(skuJson, out var sku, out var skuError))
                return Fail((int)HttpStatusCode.BadRequest, null, skuError ?? "Invalid SkuJson.");

            if (sku != null)
                data.Sku = sku;
        }

        var wait = waitForCompletion ? WaitUntil.Completed : WaitUntil.Started;

        try
        {
            var collection = _armClient.GetGenericResources();
            try
            {
                var operation = await collection
                    .CreateOrUpdateAsync(wait, rid, data, cancellationToken)
                    .ConfigureAwait(false);

                // Some extension resources (e.g., Microsoft.Insights/diagnosticSettings) can return location: null in the ARM response,
                // which makes the SDK throw during GenericResourceData deserialization. If we got here, deserialization succeeded.
                return MapSuccess(operation.Value);
            }
            catch (ArgumentNullException ex) when (waitForCompletion && ex.ParamName == "location")
            {
                // Retry without deserializing the final resource shape.
                await collection
                    .CreateOrUpdateAsync(WaitUntil.Started, rid, data, cancellationToken)
                    .ConfigureAwait(false);

                return new GenericResourceOperationResult
                {
                    Succeeded = true,
                    ResourceId = resourceId,
                    ResourceJson = null
                };
            }
        }
        catch (RequestFailedException ex)
        {
            return Fail(ex.Status, ex.ErrorCode, ex.Message);
        }
    }

    public async Task<GenericResourceOperationResult> DeleteAsync(
        string resourceId,
        bool waitForCompletion,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(resourceId))
            return Fail((int)HttpStatusCode.BadRequest, null, "ResourceId is required.");

        ResourceIdentifier rid;
        try
        {
            rid = ResourceIdentifier.Parse(resourceId);
        }
        catch (FormatException ex)
        {
            return Fail((int)HttpStatusCode.BadRequest, null, $"Invalid resource id: {ex.Message}");
        }

        var wait = waitForCompletion ? WaitUntil.Completed : WaitUntil.Started;

        try
        {
            var resource = _armClient.GetGenericResource(rid);
            await resource.DeleteAsync(wait, cancellationToken).ConfigureAwait(false);
            return new GenericResourceOperationResult
            {
                Succeeded = true,
                ResourceId = resourceId
            };
        }
        catch (RequestFailedException ex)
        {
            return Fail(ex.Status, ex.ErrorCode, ex.Message);
        }
    }

    private static GenericResourceOperationResult MapSuccess(GenericResource resource)
    {
        var d = resource.Data;
        string? resourceJson;
        try
        {
            var propsText = d.Properties == null || d.Properties.ToMemory().Length == 0
                ? null
                : d.Properties.ToString();

            var bag = new Dictionary<string, object?>
            {
                ["id"] = d.Id.ToString(),
                ["name"] = d.Name,
                ["type"] = d.ResourceType.ToString(),
                ["location"] = d.Location.ToString(),
                ["kind"] = d.Kind,
                ["tags"] = d.Tags.Count > 0 ? d.Tags : null,
                ["sku"] = d.Sku == null
                    ? null
                    : JsonSerializer.Serialize(d.Sku),
                ["properties"] = string.IsNullOrEmpty(propsText)
                    ? null
                    : JsonSerializer.Deserialize<JsonElement>(propsText)
            };
            resourceJson = JsonSerializer.Serialize(bag);
        }
        catch
        {
            resourceJson = null;
        }

        return new GenericResourceOperationResult
        {
            Succeeded = true,
            ResourceId = d.Id.ToString(),
            ResourceJson = resourceJson
        };
    }

    private static GenericResourceOperationResult Fail(int? status, string? code, string message)
    {
        return new GenericResourceOperationResult
        {
            Succeeded = false,
            HttpStatus = status ?? (int)HttpStatusCode.BadRequest,
            ErrorCode = code,
            ErrorMessage = message
        };
    }

    /// <summary>
    /// SDK types like <see cref="ResourcesSku"/> use custom JSON contracts; <see cref="JsonSerializer"/> does not populate them, which produced empty sku payloads to ARM.
    /// </summary>
    private static bool TryParseResourcesSku(string skuJson, out ResourcesSku? sku, out string? error)
    {
        sku = null;
        error = null;

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(skuJson);
        }
        catch (JsonException ex)
        {
            error = $"SkuJson is not valid JSON: {ex.Message}";
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = "SkuJson must be a JSON object.";
                return false;
            }

            var name = GetStringCaseInsensitive(root, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                error = "SkuJson must include a non-empty string property 'name' (e.g. Standard_LRS).";
                return false;
            }

            var tier = GetStringCaseInsensitive(root, "tier");
            var size = GetStringCaseInsensitive(root, "size");
            var family = GetStringCaseInsensitive(root, "family");
            var model = GetStringCaseInsensitive(root, "model");
            int? capacity = null;
            if (TryGetPropertyCaseInsensitive(root, "capacity", out var capEl) &&
                capEl.ValueKind == JsonValueKind.Number &&
                capEl.TryGetInt32(out var cap))
                capacity = cap;

            sku = new ResourcesSku
            {
                Name = name,
                Tier = tier,
                Size = size,
                Family = family,
                Model = model,
                Capacity = capacity
            };
            return true;
        }
    }

    private static string? GetStringCaseInsensitive(JsonElement obj, string propertyName)
    {
        if (!TryGetPropertyCaseInsensitive(obj, propertyName, out var el))
            return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static bool TryGetPropertyCaseInsensitive(JsonElement obj, string propertyName, out JsonElement value)
    {
        foreach (var prop in obj.EnumerateObject())
        {
            if (prop.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = prop.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
