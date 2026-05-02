using InfraMapper.Models;

namespace InfraMapper.Services;

public interface IArmGenericResourceService
{
    Task<GenericResourceOperationResult> GetAsync(string resourceId, CancellationToken cancellationToken = default);

    Task<GenericResourceOperationResult> CreateOrUpdateAsync(
        string resourceId,
        string location,
        string? propertiesJson,
        IReadOnlyDictionary<string, string>? tags,
        string? skuJson,
        string? kind,
        bool waitForCompletion,
        CancellationToken cancellationToken = default);

    Task<GenericResourceOperationResult> DeleteAsync(
        string resourceId,
        bool waitForCompletion,
        CancellationToken cancellationToken = default);
}
