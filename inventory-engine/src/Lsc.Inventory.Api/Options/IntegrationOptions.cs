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
