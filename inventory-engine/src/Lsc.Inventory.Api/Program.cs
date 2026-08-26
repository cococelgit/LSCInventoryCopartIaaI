using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;
using Lsc.Inventory.Api.Workers;
using Polly;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using Microsoft.AspNetCore.WebUtilities;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<ApibaraOptions>()
    .Bind(builder.Configuration.GetSection(ApibaraOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services
    .AddOptions<SyncOptions>()
    .Bind(builder.Configuration.GetSection(SyncOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<IaaIPilotOptions>()
    .Bind(builder.Configuration.GetSection(IaaIPilotOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services
    .AddOptions<CopartExcelOptions>()
    .Bind(builder.Configuration.GetSection(CopartExcelOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services
    .AddOptions<PersistenceOptions>()
    .Bind(builder.Configuration.GetSection(PersistenceOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services
    .AddOptions<BlobAuditOptions>()
    .Bind(builder.Configuration.GetSection(BlobAuditOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services.AddHttpClient<IApibaraClient, ApibaraClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApibaraOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
})
.AddResilienceHandler("apibara", pipeline =>
{
    pipeline.AddTimeout(TimeSpan.FromSeconds(30));
});

var persistenceProvider = builder.Configuration.GetValue<string>($"{PersistenceOptions.SectionName}:Provider") ?? "InMemory";
if (string.Equals(persistenceProvider, "Postgres", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IInventorySnapshotStore, PostgresSnapshotStore>();
}
else
{
    builder.Services.AddSingleton<IInventorySnapshotStore, InMemorySnapshotStore>();
}
builder.Services.AddScoped<IInventorySyncProcessor, InventorySyncProcessor>();
builder.Services.AddScoped<IIaaIPilotProcessor, IaaIPilotProcessor>();
builder.Services.AddScoped<ICopartExcelSnapshotAdapter, CopartExcelSnapshotAdapter>();
builder.Services.AddScoped<ICopartExcelSnapshotSource, CopartBlobSnapshotSource>();
builder.Services.AddScoped<ICopartExcelSnapshotProcessor, CopartExcelSnapshotProcessor>();
builder.Services.AddHostedService<InventorySyncWorker>();

var app = builder.Build();
var inventoryReadToken = builder.Configuration["InventoryApi:Token"] ?? Environment.GetEnvironmentVariable("INVENTORY_API_TOKEN");

static bool HasValidReadToken(HttpContext context, string? expectedToken)
{
    if (string.IsNullOrWhiteSpace(expectedToken)) return false;
    var header = context.Request.Headers.Authorization.ToString();
    if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;
    var supplied = header["Bearer ".Length..].Trim();
    return CryptographicOperations.FixedTimeEquals(
        Encoding.UTF8.GetBytes(supplied),
        Encoding.UTF8.GetBytes(expectedToken));
}

static string? SafePhotoUrl(string? candidate)
{
    if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return null;
    var allowedHost = uri.Host.Equals("vis.iaai.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".copart.com", StringComparison.OrdinalIgnoreCase);
    if (uri.Scheme != Uri.UriSchemeHttps || !allowedHost) return null;
    if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return null;
    if (!string.IsNullOrEmpty(uri.Query))
    {
        if (!uri.Host.Equals("vis.iaai.com", StringComparison.OrdinalIgnoreCase)) return null;
        var allowedParameters = new HashSet<string>(["imageKeys", "width", "height", "format"], StringComparer.OrdinalIgnoreCase);
        if (QueryHelpers.ParseQuery(uri.Query).Keys.Any(key => !allowedParameters.Contains(key))) return null;
    }
    return uri.ToString();
}

static PublicInventoryVehicle ToPublicVehicle(StoredVehicleSnapshot snapshot)
{
    var vehicle = snapshot.Vehicle;
    var media = (vehicle.Media?.Items ?? Array.Empty<AuctionMediaItem>())
        .Select(item => new { Url = SafePhotoUrl(item.Large) ?? SafePhotoUrl(item.Thumb), item.Type })
        .Where(item => item.Url is not null)
        .Select(item => new PublicMediaItem(item.Url!, item.Type))
        .DistinctBy(item => item.Url)
        .Take(20)
        .ToArray();
    var photos = media.Select(item => item.Url)
        .Concat(vehicle.Media?.Photos ?? Array.Empty<string>())
        .Select(SafePhotoUrl)
        .Where(url => url is not null)
        .Cast<string>()
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .Take(20)
        .ToArray();
    var description = vehicle.Details?.VehicleDescription;
    var information = vehicle.Details?.VehicleInformation;
    var sale = vehicle.Details?.SaleInformation;
    return new PublicInventoryVehicle
    {
        Lot = vehicle.LotNumber ?? snapshot.Identity,
        Platform = vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown",
        ObservedAt = snapshot.ObservedAt,
        Title = vehicle.Title,
        Year = vehicle.Year,
        Make = vehicle.Make,
        Model = vehicle.Model,
        Series = description?.Series,
        VehicleType = vehicle.VehicleType,
        BodyStyle = vehicle.VehicleSpecs?.BodyStyle ?? description?.BodyStyle,
        Color = vehicle.Color,
        FuelType = vehicle.FuelType,
        Transmission = vehicle.Transmission,
        DriveType = vehicle.DriveType,
        Odometer = vehicle.Odometer,
        OdometerKm = vehicle.OdometerInfo?.Kilometers,
        OdometerStatus = vehicle.OdometerInfo?.Status,
        Damage = vehicle.Damage,
        SecondaryDamage = vehicle.Condition?.SecondaryDamage,
        LossType = vehicle.Condition?.Loss,
        StartCode = vehicle.Condition?.RunCondition?.Label ?? vehicle.Condition?.RunCondition?.Value,
        HasKey = vehicle.Condition?.HasKey,
        AuctionAt = vehicle.Auction?.AuctionAt,
        LotStatus = vehicle.Auction?.LotStatus,
        LotSubStatus = vehicle.Auction?.LotSubStatus,
        IsBuyNow = vehicle.Auction?.IsBuyNow,
        IsTimed = vehicle.Auction?.IsTimed,
        CurrentBidUsd = vehicle.Pricing?.CurrentBidUsd,
        PreBidUsd = vehicle.Pricing?.PreBidUsd,
        BuyNowUsd = vehicle.Pricing?.BuyNowUsd,
        EstimatedPriceFromUsd = vehicle.Pricing?.EstimatedCost?.FromUsd,
        EstimatedPriceToUsd = vehicle.Pricing?.EstimatedCost?.ToUsd,
        EstimatedPriceText = vehicle.Pricing?.EstimatedCost?.Text,
        ActualCashValueUsd = ParseMoney(sale?.ActualCashValue),
        EstimatedRepairCostUsd = ParseMoney(sale?.EstimatedRepairCost),
        Location = vehicle.Location?.Display,
        SendFrom = vehicle.Location?.SendFrom,
        State = vehicle.Location?.State,
        FacilityId = vehicle.Location?.FacilityId,
        SellingBranch = sale?.SellingBranch,
        Lane = sale?.Lane,
        Aisle = sale?.Aisle,
        SellerName = vehicle.Seller?.Name ?? sale?.Seller,
        SellerType = vehicle.Seller?.Type ?? sale?.SellerType,
        TitleType = vehicle.SaleDocument?.Name,
        SaleDocumentType = vehicle.SaleDocument?.Type,
        SaleDocumentGroup = vehicle.SaleDocument?.Group,
        SaleDocumentPending = vehicle.SaleDocument?.IsPending,
        SaleDocumentExport = vehicle.SaleDocument?.Export,
        SaleDocumentRegistration = vehicle.SaleDocument?.Registration,
        TitleBrand = information?.TitleBrand,
        TitleNotes = information?.TitleNotes,
        EngineSizeLiters = vehicle.VehicleSpecs?.Engine?.SizeLiters,
        EngineHorsepower = vehicle.VehicleSpecs?.Engine?.Horsepower,
        EngineLayout = vehicle.VehicleSpecs?.Engine?.Layout,
        EngineDescription = vehicle.VehicleSpecs?.Engine?.Raw,
        Cylinders = description?.Cylinders,
        Airbags = vehicle.VehicleSpecs?.Airbags,
        RestraintSystem = vehicle.VehicleSpecs?.RestraintSystem,
        VinStatus = information?.VinStatus ?? description?.VinStatus,
        VehicleClass = description?.VehicleClass,
        VehicleScore = description?.VehicleScore,
        ManufacturedIn = description?.ManufacturedIn,
        Options = description?.Options,
        Has360 = vehicle.Media?.Has360,
        HasVideo = vehicle.Media?.HasVideo ?? media.Any(item => string.Equals(item.Type, "video", StringComparison.OrdinalIgnoreCase)),
        Photos = photos,
        Media = media
    };
}

static decimal? ParseMoney(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return null;
    var sanitized = new string(value.Where(character => char.IsDigit(character) || character is '.' or '-').ToArray());
    return decimal.TryParse(sanitized, NumberStyles.Number | NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)
        ? result
        : null;
}

app.MapGet("/healthz", () => Results.Ok(new
{
    status = "healthy",
    service = "lsc-inventory-engine",
    utc = DateTimeOffset.UtcNow
}));

app.MapGet("/readyz", () => Results.Ok(new
{
    status = "ready",
    persistence = persistenceProvider,
    database = string.Equals(persistenceProvider, "Postgres", StringComparison.OrdinalIgnoreCase) ? "configured" : "not-configured"
}));

app.MapGet("/api/v1/inventory/recent", async (HttpContext context, IInventorySnapshotStore store, int? take, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var limit = Math.Clamp(take ?? 1000, 1, 1000);
    var snapshots = (await store.GetRecentAsync(5000, cancellationToken))
        .Where(snapshot => AuctionEligibilityEvaluator.Evaluate(snapshot.Vehicle).LoadToSystem)
        .Take(limit)
        .ToArray();
    var vehicles = snapshots.Select(ToPublicVehicle).ToArray();
    var generatedAt = snapshots.Length > 0 ? snapshots.Max(snapshot => snapshot.ObservedAt) : DateTimeOffset.UtcNow;
    return Results.Ok(new PublicInventoryResponse("lsc-inventory-postgres", generatedAt, vehicles));
});

app.MapGet("/api/v1/inventory/vehicle/{lot}", async (HttpContext context, IInventorySnapshotStore store, string lot, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var snapshot = (await store.GetRecentAsync(5000, cancellationToken))
        .FirstOrDefault(item => string.Equals(item.Vehicle.LotNumber, lot, StringComparison.OrdinalIgnoreCase) &&
            AuctionEligibilityEvaluator.Evaluate(item.Vehicle).LoadToSystem);
    return snapshot is null ? Results.NotFound() : Results.Ok(ToPublicVehicle(snapshot));
});

app.MapGet("/internal/eligibility/discarded", async (
    HttpContext context,
    IInventorySnapshotStore store,
    int? page,
    int? pageSize,
    string? ruleCode,
    string? query,
    CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var result = await store.GetDiscardedEligibilityDecisionsAsync(page ?? 1, pageSize ?? 25, ruleCode, query, cancellationToken);
    return Results.Ok(result);
});

app.MapGet("/internal/validation", async (HttpContext context, IInventorySnapshotStore store, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var report = await store.GetValidationReportAsync(cancellationToken);
    return Results.Ok(report);
});

app.MapGet("/api/v1/usage", async (HttpContext context, IApibaraClient client, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var usage = await client.GetUsageAsync(cancellationToken);
    return Results.Ok(usage);
});

app.MapPost("/internal/sync/run", async (HttpContext context, IInventorySyncProcessor processor, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var result = await processor.RunOnceAsync(cancellationToken);
    return Results.Ok(result);
});

if (args.Contains("--bootstrap-db", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    if (store is not PostgresSnapshotStore postgresStore)
    {
        throw new InvalidOperationException("Database bootstrap requires Persistence:Provider=Postgres.");
    }

    await postgresStore.BootstrapRuntimePrincipalAsync(CancellationToken.None);
    Console.WriteLine("Database bootstrap completed.");
    return;
}

if (args.Contains("--validation-report", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    var report = await store.GetValidationReportAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(report));
    return;
}

if (args.Contains("--storage-diagnostics", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    if (store is not PostgresSnapshotStore postgresStore)
    {
        throw new InvalidOperationException("Storage diagnostics require Persistence:Provider=Postgres.");
    }

    Console.WriteLine(await postgresStore.GetStorageDiagnosticsAsync(CancellationToken.None));
    return;
}

if (args.Contains("--media-diagnostic", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    if (store is not PostgresSnapshotStore postgresStore)
    {
        throw new InvalidOperationException("Media diagnostics require Persistence:Provider=Postgres.");
    }

    Console.WriteLine(await postgresStore.GetPublicMediaManifestAsync(CancellationToken.None));
    return;
}

var copartFileIndex = Array.FindIndex(args, argument => string.Equals(argument, "--copart-excel-file", StringComparison.OrdinalIgnoreCase));
if (copartFileIndex >= 0)
{
    if (copartFileIndex + 1 >= args.Length)
        throw new ArgumentException("--copart-excel-file requires a CSV path.");

    await using var scope = app.Services.CreateAsyncScope();
    var processor = scope.ServiceProvider.GetRequiredService<ICopartExcelSnapshotProcessor>();
    var snapshot = await CopartSnapshotFile.OpenAsync(args[copartFileIndex + 1], CancellationToken.None);
    await using var content = snapshot.Content;
    var result = await processor.ProcessAsync(snapshot, CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    if (!result.Processed || !result.IsComplete || result.Errors > 0)
    {
        Environment.ExitCode = 1;
    }

    return;
}

if (args.Contains("--copart-excel-run", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var processor = scope.ServiceProvider.GetRequiredService<ICopartExcelSnapshotProcessor>();
    var result = await processor.RunLatestAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    if (!result.Processed || !result.IsComplete || result.Errors > 0)
    {
        Environment.ExitCode = 1;
    }

    return;
}

if (args.Contains("--run-once", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var processor = scope.ServiceProvider.GetRequiredService<IInventorySyncProcessor>();
    var result = await processor.RunOnceAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    if (result.Failures.Count > 0)
    {
        Environment.ExitCode = 1;
    }

    return;
}

if (args.Contains("--iaai-pilot", StringComparer.OrdinalIgnoreCase) || builder.Configuration.GetValue<bool>("IaaIPilot:RunOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var processor = scope.ServiceProvider.GetRequiredService<IIaaIPilotProcessor>();
    var result = await processor.RunAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    if (result.Failures.Count > 0 || result.Observed == 0) Environment.ExitCode = 1;
    return;
}

if (builder.Configuration.GetValue<bool>("Maintenance:RunEmptyReconciliation"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    var platform = builder.Configuration["Maintenance:Platform"] ?? throw new InvalidOperationException("Maintenance:Platform is required.");
    var result = await store.ReconcileSourceAsync(platform, [], true, DateTimeOffset.UtcNow, CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    return;
}

await app.RunAsync();
