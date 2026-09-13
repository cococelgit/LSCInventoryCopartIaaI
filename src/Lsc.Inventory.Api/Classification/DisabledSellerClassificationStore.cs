using Lsc.Inventory.Api.Normalization;

namespace Lsc.Inventory.Api.Classification;

public sealed class DisabledSellerClassificationStore : ISellerClassificationStore
{
    public Task<SellerClassification?> GetAsync(string platform, string normalizedName, CancellationToken cancellationToken) =>
        Task.FromResult<SellerClassification?>(null);

    public Task UpsertAsync(string platform, string normalizedName, string rawName, SellerClassification classification, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
