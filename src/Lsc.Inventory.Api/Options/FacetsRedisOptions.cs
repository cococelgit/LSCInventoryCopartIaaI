using System.ComponentModel.DataAnnotations;

namespace Lsc.Inventory.Api.Options;

public sealed class FacetsRedisOptions
{
    public const string SectionName = "FacetsRedis";

    public bool Enabled { get; init; }
    public string Endpoint { get; init; } = string.Empty;
    public string ManagedIdentityClientId { get; init; } = string.Empty;
    public string KeyPrefix { get; init; } = "lsc:facets:v2:";

    [Range(5, 60)]
    public int TimeToLiveSeconds { get; init; } = 15;

    [Range(1000, 10000)]
    public int DistributedLockMilliseconds { get; init; } = 4000;

    public bool IsConfigured => Enabled &&
        !string.IsNullOrWhiteSpace(Endpoint) &&
        !string.IsNullOrWhiteSpace(ManagedIdentityClientId) &&
        !string.IsNullOrWhiteSpace(KeyPrefix);
}
