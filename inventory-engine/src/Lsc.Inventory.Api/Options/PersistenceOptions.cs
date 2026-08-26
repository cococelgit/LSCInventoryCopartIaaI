using System.ComponentModel.DataAnnotations;

namespace Lsc.Inventory.Api.Options;

public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    [Required]
    public string Provider { get; init; } = "InMemory";

    [Required]
    public string PostgreSqlHost { get; init; } = string.Empty;

    [Required]
    public string Database { get; init; } = "lsc_inventory";

    [Required]
    public string ManagedIdentityClientId { get; init; } = string.Empty;

    [Required]
    public string DatabaseUser { get; init; } = "id-lsc-inventory-runtime-prod";

    public string? AccessToken { get; init; }

    public bool RunMigrations { get; init; }

    [Required]
    public string RuntimePrincipalName { get; init; } = "id-lsc-inventory-runtime-prod";

    [Required]
    public string RuntimePrincipalObjectId { get; init; } = string.Empty;

    public string? PreviousRuntimePrincipalName { get; init; }

    [Range(1, 120)]
    public int CommandTimeoutSeconds { get; init; } = 30;
}

public sealed class BlobAuditOptions
{
    public const string SectionName = "BlobAudit";

    [Required]
    public string AccountUrl { get; init; } = string.Empty;

    [Required]
    public string ContainerName { get; init; } = "raw-apibara";
}
