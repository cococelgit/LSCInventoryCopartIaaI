using System.ComponentModel.DataAnnotations;

namespace Lsc.Inventory.Api.Options;

public sealed class ApibaraOptions
{
    public const string SectionName = "Apibara";

    [Required]
    [Url]
    public string BaseUrl { get; init; } = "https://apibara.tech/api/v1/vehicle-auction/";

    [Required]
    public string ApiKey { get; init; } = string.Empty;

    [Range(1, 20)]
    public int PageSize { get; init; } = 20;

    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    /// <summary>Total number of attempts for transient provider failures, including the first request.</summary>
    [Range(1, 5)]
    public int RetryMaxAttempts { get; init; } = 3;

    [Range(50, 5000)]
    public int RetryBaseDelayMilliseconds { get; init; } = 500;

    [Range(100, 30000)]
    public int RetryMaxDelayMilliseconds { get; init; } = 4000;
}

public sealed class AuctionsApiOptions
{
    public const string SectionName = "AuctionsApi";

    [Url]
    public string BaseUrl { get; init; } = "https://auctionsapi.com/api/";

    /// <summary>
    /// Intentionally false until the Owner authorizes an isolated shadow run.
    /// Registering the client must never change the source used by an existing Job.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>Second gate: even with a valid token, canonical writes stay disabled until explicitly approved.</summary>
    public bool AllowWrites { get; init; }

    public string ApiKey { get; init; } = string.Empty;

    [Range(1, 1000)]
    public int PageSize { get; init; } = 1000;

    [Range(1, 4320)]
    public int DefaultOverlapMinutes { get; init; } = 120;

    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; init; } = 30;

    [Range(1, 100000)]
    public int InitialImportMaxLots { get; init; } = 100000;

    [Range(1, 500)]
    public int InitialImportMaxRequests { get; init; } = 120;

    public bool IsConfigured => Enabled && !string.IsNullOrWhiteSpace(ApiKey);
}

public sealed class SyncOptions
{
    public const string SectionName = "Sync";

    [Required]
    public string[] Platforms { get; init; } = [];

    [Required]
    public string[] States { get; init; } = [];

    [Range(1, 120)]
    public int IntervalMinutes { get; init; } = 15;

    [Range(1, 20)]
    public int PagesPerScope { get; init; } = 1;

    [RegularExpression("^[0-9]+$")]
    public string? FacilityId { get; init; }

    public string[] FacilityIds { get; init; } = [];

    public bool UseAllFacilitiesForState { get; init; }

    public bool EnrichVehicleDetails { get; init; } = true;

    [Range(0, 100)]
    public int DetailEnrichmentLimitPerRun { get; init; }

    public bool CaptureUsage { get; init; }

    public bool Enabled { get; init; }
}

public sealed class IaaIPilotOptions
{
    public const string SectionName = "IaaIPilot";

    public bool Enabled { get; init; }

    public bool RunOnStartup { get; init; }

    [Range(1, 10000)]
    public int MaxVehicles { get; init; } = 1000;

    [Range(1, 1000)]
    public int MaxListRequests { get; init; } = 60;

    public string LotSubStatus { get; init; } = "Open";

    public bool EnrichDetails { get; init; }

    [Range(0, 10000)]
    public int DetailEnrichmentLimit { get; init; }
}

public sealed class IaaINationalOptions
{
    public const string SectionName = "IaaINational";

    public bool Enabled { get; init; }

    /// <summary>
    /// Runs the national processor once at process startup. Container Apps Jobs use this
    /// configuration switch so Azure CLI never needs to parse an application argument
    /// beginning with a double dash.
    /// </summary>
    public bool RunOnStartup { get; init; }

    public string LotSubStatus { get; init; } = "Open";

    [Range(1, 100)]
    public int PagesPerRun { get; init; } = 50;

    [Range(1, 5000)]
    public int BackfillPagesPerRun { get; init; } = 100;

    [Range(1, 5000)]
    public int BackfillMaxRequestsPerRun { get; init; } = 180;

    [Range(1, 100)]
    public int MaintenancePagesPerRun { get; init; } = 3;

    [Range(1, 200)]
    public int MaintenanceMaxRequestsPerRun { get; init; } = 15;

    [Range(1, 200)]
    public int MaxRequestsPerRun { get; init; } = 80;

    public bool EnrichVehicleDetails { get; init; } = true;

    [Range(0, 1000)]
    public int DetailEnrichmentLimitPerRun { get; init; } = 20;

    [Range(0, 1000)]
    public int BackfillDetailEnrichmentLimitPerRun { get; init; } = 20;

    [Range(0, 1000)]
    public int MaintenanceDetailEnrichmentLimitPerRun { get; init; } = 6;

    [Range(1, 720)]
    public int LeaseMinutes { get; init; } = 35;

    public bool CaptureUsage { get; init; } = true;

    [Range(0, 50000)]
    public int MinimumRemainingRequests { get; init; } = 2000;
}
