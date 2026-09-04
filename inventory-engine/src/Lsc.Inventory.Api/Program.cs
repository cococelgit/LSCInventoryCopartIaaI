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

builder.Services.AddHttpClient<ICopartMediaResolver, CopartMediaResolver>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
});
builder.Services.AddHttpClient("copart-media-proxy", client =>
{
    client.Timeout = TimeSpan.FromSeconds(20);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });

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
builder.Services.AddScoped<ICopartMediaEnrichmentProcessor, CopartMediaEnrichmentProcessor>();
builder.Services.AddScoped<ICopartTitleBackfillProcessor, CopartTitleBackfillProcessor>();
builder.Services.AddScoped<ICopartFutureBodyStyleBackfillProcessor, CopartFutureBodyStyleBackfillProcessor>();
builder.Services.AddScoped<ICopartAuctionHistoryBackfillProcessor, CopartAuctionHistoryBackfillProcessor>();
builder.Services.AddScoped<ICopartScoringBackfillProcessor, CopartScoringBackfillProcessor>();
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

static bool IsApprovedCopartMediaUrl(string? candidate)
{
    if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)) return false;
    return uri.Scheme == Uri.UriSchemeHttps &&
           (uri.Host.Equals("copart.com", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".copart.com", StringComparison.OrdinalIgnoreCase)) &&
           string.IsNullOrEmpty(uri.UserInfo) &&
           string.IsNullOrEmpty(uri.Fragment);
}

static string CreateCopartMediaSignature(string platform, string lot, int photoIndex, long expiresAtUnix, string token)
{
    var payload = $"{platform.ToLowerInvariant()}|{lot}|{photoIndex}|{expiresAtUnix}";
    using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(token));
    return WebEncoders.Base64UrlEncode(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
}

static bool HasValidCopartMediaSignature(string platform, string lot, int photoIndex, long expiresAtUnix, string? signature, string? token)
{
    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(signature) || expiresAtUnix < DateTimeOffset.UtcNow.ToUnixTimeSeconds()) return false;
    var expected = CreateCopartMediaSignature(platform, lot, photoIndex, expiresAtUnix, token);
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(signature), Encoding.UTF8.GetBytes(expected));
}

static string? MaskVin(string? vin)
{
    var normalized = new string((vin ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
    if (normalized.Length < 7) return null;
    return $"***********{normalized[^6..]}";
}

static string? RawText(AuctionVehicle vehicle, string field)
{
    if (vehicle.RawSource is not { } raw || raw.ValueKind != System.Text.Json.JsonValueKind.Object ||
        !raw.TryGetProperty(field, out var value) || value.ValueKind != System.Text.Json.JsonValueKind.String) return null;
    var text = value.GetString()?.Trim();
    return string.IsNullOrWhiteSpace(text) ? null : text;
}

static decimal? RawDecimal(AuctionVehicle vehicle, string field)
{
    var raw = RawText(vehicle, field);
    return decimal.TryParse(raw, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var value) ? value : null;
}

static bool? RawYesNo(AuctionVehicle vehicle, string field) => RawText(vehicle, field)?.ToUpperInvariant() switch
{
    "YES" or "Y" or "TRUE" => true,
    "NO" or "N" or "FALSE" => false,
    _ => null
};

static string? RawTitleCode(AuctionVehicle vehicle)
{
    var fromRaw = RawText(vehicle, "Sale Title Type");
    if (!string.IsNullOrWhiteSpace(fromRaw)) return fromRaw;
    if (vehicle.AdditionalData is not null && vehicle.AdditionalData.TryGetValue("source_title_type_code", out var code) && code.ValueKind == System.Text.Json.JsonValueKind.String)
        return code.GetString()?.Trim();
    if (vehicle.TitleNotes is { ValueKind: System.Text.Json.JsonValueKind.Object } notes && notes.TryGetProperty("sale_title_type_code", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String)
        return value.GetString()?.Trim();
    return null;
}

static CopartTitleDefinition? ResolveCopartTitle(AuctionVehicle vehicle)
{
    if (!string.Equals(vehicle.Platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase)) return null;
    return CopartTitleCatalog.TryGet(RawTitleCode(vehicle), out var definition) ? definition : null;
}

static PublicInventoryVehicle ToPublicVehicle(StoredVehicleSnapshot snapshot, Uri requestBaseUri, string? mediaSigningToken)
{
    var vehicle = snapshot.Vehicle;
    var platform = vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown";
    var lot = vehicle.LotNumber ?? snapshot.Identity;
    var copartTitle = ResolveCopartTitle(vehicle);
    var titleCode = RawTitleCode(vehicle);
    var isCopart = string.Equals(platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase);
    var sourcePhotos = vehicle.Media?.Photos ?? Array.Empty<string>();
    var photos = isCopart
        ? sourcePhotos
            .Select((source, index) => new { source, index })
            .Where(item => IsApprovedCopartMediaUrl(item.source) && !string.IsNullOrWhiteSpace(mediaSigningToken))
            .Select(item =>
            {
                var expires = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();
                var signature = CreateCopartMediaSignature(platform, lot, item.index, expires, mediaSigningToken!);
                return new Uri(requestBaseUri, $"/api/v1/inventory/media/{Uri.EscapeDataString(platform)}/{Uri.EscapeDataString(lot)}/{item.index}?expires={expires}&sig={Uri.EscapeDataString(signature)}").ToString();
            })
            .ToArray()
        : sourcePhotos
            .Select(SafePhotoUrl)
            .Where(url => url is not null)
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    return new PublicInventoryVehicle(
        lot,
        platform,
        snapshot.ObservedAt,
        vehicle.Title,
        vehicle.Year,
        vehicle.Make,
        vehicle.Model,
        vehicle.VehicleType,
        vehicle.Color,
        vehicle.FuelType,
        vehicle.Transmission,
        vehicle.DriveType,
        vehicle.Odometer,
        vehicle.Damage,
        vehicle.Auction?.AuctionAt,
        vehicle.Auction?.LotStatus,
        vehicle.Pricing?.CurrentBidUsd,
        vehicle.Pricing?.BuyNowUsd,
        vehicle.Location?.Display,
        vehicle.Location?.State,
        copartTitle?.EnglishDescription ?? vehicle.SaleDocument?.Name ?? vehicle.Title,
        titleCode,
        copartTitle?.SpanishDescription,
        string.IsNullOrWhiteSpace(titleCode) || !string.Equals(vehicle.Platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase) ? null : copartTitle is null ? "unmapped" : "mapped",
        vehicle.SaleDocument?.State ?? RawText(vehicle, "Sale Title State"),
        vehicle.Location?.FacilityId,
        MaskVin(vehicle.Vin ?? RawText(vehicle, "VIN")),
        vehicle.Seller?.Name ?? RawText(vehicle, "Seller Name"),
        vehicle.VehicleSpecs?.Trim ?? RawText(vehicle, "Trim"),
        vehicle.VehicleSpecs?.BodyStyle ?? RawText(vehicle, "Body Style"),
        vehicle.VehicleSpecs?.Engine ?? RawText(vehicle, "Engine"),
        vehicle.VehicleSpecs?.Cylinders ?? RawText(vehicle, "Cylinders"),
        vehicle.Pricing?.EstimatedRetailValueUsd ?? RawDecimal(vehicle, "Est. Retail Value"),
        vehicle.Pricing?.RepairCostUsd ?? RawDecimal(vehicle, "Repair cost"),
        vehicle.Condition?.LotConditionCode ?? RawText(vehicle, "Lot Cond. Code"),
        isCopart ? vehicle.Condition?.RunCondition?.Normalized ?? "UNVERIFIED" : vehicle.Condition?.RunCondition?.Label,
        isCopart ? vehicle.Condition?.RunCondition?.Raw ?? RawText(vehicle, "Runs/Drives") : null,
        vehicle.Condition?.HasKey ?? RawYesNo(vehicle, "Has Keys-Yes or No"),
        photos);
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
    var requestBaseUri = new Uri($"{context.Request.Scheme}://{context.Request.Host}");
    var vehicles = snapshots.Select(snapshot => ToPublicVehicle(snapshot, requestBaseUri, inventoryReadToken)).ToArray();
    var generatedAt = snapshots.Length > 0 ? snapshots.Max(snapshot => snapshot.ObservedAt) : DateTimeOffset.UtcNow;
    return Results.Ok(new PublicInventoryResponse("lsc-inventory-postgres", generatedAt, vehicles));
});

app.MapGet("/api/v1/inventory/browse", async (
    HttpContext context,
    IInventorySnapshotStore store,
    string? platform,
    string? query,
    int? page,
    int? pageSize,
    string? sort,
    int? yearFrom,
    int? yearTo,
    decimal? maximumBid,
    bool? requireBid,
    bool? requirePhotos,
    bool? includeSpecialTitles,
    string[]? makes,
    string[]? models,
    string[]? facilities,
    string[]? states,
    string[]? vehicleTypes,
    string[]? damages,
    string[]? titleTypes,
    string[]? drives,
    string[]? transmissions,
    string[]? fuels,
    decimal? odometerFrom,
    decimal? odometerTo,
    DateOnly? auctionFrom,
    DateOnly? auctionTo,
    decimal? estimatedTotalFrom,
    decimal? estimatedTotalTo,
    CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var requested = new InventoryBrowseQuery(
        platform,
        query,
        page ?? 1,
        pageSize ?? 24,
        sort ?? "auction",
        yearFrom,
        yearTo,
        maximumBid,
        requireBid ?? false,
        requirePhotos ?? false,
        includeSpecialTitles ?? false,
        makes,
        models,
        facilities,
        states,
        vehicleTypes,
        damages,
        titleTypes,
        drives,
        transmissions,
        fuels,
        odometerFrom,
        odometerTo,
        auctionFrom,
        auctionTo,
        estimatedTotalFrom,
        estimatedTotalTo);
    var result = await store.GetPageAsync(requested, cancellationToken);
    var requestBaseUri = new Uri($"{context.Request.Scheme}://{context.Request.Host}");
    var vehicles = result.Vehicles.Select(snapshot => ToPublicVehicle(snapshot, requestBaseUri, inventoryReadToken)).ToArray();
    var generatedAt = result.Vehicles.Count > 0 ? result.Vehicles.Max(snapshot => snapshot.ObservedAt) : DateTimeOffset.UtcNow;
    return Results.Ok(new PublicInventoryPageResponse("lsc-inventory-postgres", generatedAt, result.Page, result.PageSize, result.Total, result.TotalPages, vehicles));
});

app.MapGet("/api/v1/inventory/vehicle/{lot}", async (HttpContext context, IInventorySnapshotStore store, string lot, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var snapshot = (await store.GetRecentAsync(5000, cancellationToken))
        .FirstOrDefault(item => string.Equals(item.Vehicle.LotNumber, lot, StringComparison.OrdinalIgnoreCase) &&
            AuctionEligibilityEvaluator.Evaluate(item.Vehicle).LoadToSystem);
    var requestBaseUri = new Uri($"{context.Request.Scheme}://{context.Request.Host}");
    return snapshot is null ? Results.NotFound() : Results.Ok(ToPublicVehicle(snapshot, requestBaseUri, inventoryReadToken));
});

app.MapGet("/api/v1/inventory/media/{platform}/{lot}/{photoIndex:int}", async (
    HttpContext context,
    IInventorySnapshotStore store,
    IHttpClientFactory httpClientFactory,
    string platform,
    string lot,
    int photoIndex,
    long expires,
    string? sig,
    CancellationToken cancellationToken) =>
{
    if (!string.Equals(platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase) ||
        photoIndex < 0 ||
        !HasValidCopartMediaSignature(platform, lot, photoIndex, expires, sig, inventoryReadToken))
    {
        return Results.NotFound();
    }

    var snapshot = await store.GetByPlatformAndLotAsync(platform, lot, cancellationToken);
    if (snapshot is null || !AuctionEligibilityEvaluator.Evaluate(snapshot.Vehicle).LoadToSystem) return Results.NotFound();
    var sourceUrl = snapshot.Vehicle.Media?.Photos?.ElementAtOrDefault(photoIndex);
    if (!IsApprovedCopartMediaUrl(sourceUrl)) return Results.NotFound();

    using var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl);
    using var response = await httpClientFactory.CreateClient("copart-media-proxy").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    if (!response.IsSuccessStatusCode) return Results.StatusCode(StatusCodes.Status502BadGateway);
    var contentType = response.Content.Headers.ContentType?.MediaType;
    if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)) return Results.StatusCode(StatusCodes.Status502BadGateway);
    const int maximumBytes = 12 * 1024 * 1024;
    if (response.Content.Headers.ContentLength is long length && length > maximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
    if (bytes.Length > maximumBytes) return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
    context.Response.Headers.CacheControl = "public,max-age=86400,stale-while-revalidate=604800";
    return Results.File(bytes, contentType);
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

if (args.Contains("--copart-publication-report", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    if (store is not PostgresSnapshotStore postgresStore)
    {
        throw new InvalidOperationException("Copart publication report requires Persistence:Provider=Postgres.");
    }

    Console.WriteLine(await postgresStore.GetCopartPublicationReportAsync(CancellationToken.None));
    var holdSeconds = Math.Clamp(builder.Configuration.GetValue<int?>("CopartExcel:PublicationReportHoldSeconds") ?? 60, 30, 120);
    await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
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

    Console.Error.WriteLine(await postgresStore.GetStorageDiagnosticsAsync(CancellationToken.None));
    var holdSeconds = Math.Clamp(builder.Configuration.GetValue<int?>("CopartExcel:DiagnosticHoldSeconds") ?? 60, 30, 120);
    await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
    return;
}

var lotMediaDiagnosticArgument = args.FirstOrDefault(argument => argument.StartsWith("--copart-lot-media-diagnostics", StringComparison.OrdinalIgnoreCase));
if (lotMediaDiagnosticArgument is not null)
{
    var inlineLotNumber = lotMediaDiagnosticArgument["--copart-lot-media-diagnostics".Length..].Trim().TrimStart('=');
    var lotMediaDiagnosticIndex = Array.FindIndex(args, argument => string.Equals(argument, "--copart-lot-media-diagnostics", StringComparison.OrdinalIgnoreCase));
    var lotNumber = !string.IsNullOrWhiteSpace(inlineLotNumber)
        ? inlineLotNumber
        : lotMediaDiagnosticIndex >= 0 && lotMediaDiagnosticIndex + 1 < args.Length
            ? args[lotMediaDiagnosticIndex + 1]
            : null;
    if (string.IsNullOrWhiteSpace(lotNumber))
        throw new ArgumentException("--copart-lot-media-diagnostics requires a Copart lot number.");

    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    if (store is not PostgresSnapshotStore postgresStore)
        throw new InvalidOperationException("Copart lot media diagnostics require Persistence:Provider=Postgres.");

    Console.Error.WriteLine(await postgresStore.GetCopartLotMediaDiagnosticsAsync(lotNumber, CancellationToken.None));
    var storedLot = await store.GetByPlatformAndLotAsync(InventorySourcePolicy.CopartExcelSource, lotNumber, CancellationToken.None);
    if (storedLot is not null)
    {
        var resolver = scope.ServiceProvider.GetRequiredService<ICopartMediaResolver>();
        var resolution = await resolver.ResolveAsync(storedLot.Vehicle, CancellationToken.None);
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            CatalogResolution = new
            {
                resolution.Resolved,
                resolution.GalleryImages,
                resolution.HdImages,
                resolution.FailureCode
            }
        }));
    }
    var holdSeconds = Math.Clamp(builder.Configuration.GetValue<int?>("CopartExcel:DiagnosticHoldSeconds") ?? 60, 30, 120);
    await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
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

if (args.Contains("--copart-reset", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    var result = await store.ResetCopartInventoryAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    return;
}

if (args.Contains("--copart-reset-and-fresh-future", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    var reset = await store.ResetCopartInventoryAsync(CancellationToken.None);
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { stage = "reset", result = reset }));
    var processor = scope.ServiceProvider.GetRequiredService<ICopartExcelSnapshotProcessor>();
    var load = await processor.RunLatestFutureOnlyAsync(CancellationToken.None);
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { stage = "fresh-future-load", result = load }));
    if (!load.Processed || load.Errors > 0) Environment.ExitCode = 1;
    return;
}

if (args.Contains("--copart-media-probe", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var source = scope.ServiceProvider.GetRequiredService<ICopartExcelSnapshotSource>();
    var adapter = scope.ServiceProvider.GetRequiredService<ICopartExcelSnapshotAdapter>();
    var result = await CopartMediaSnapshotProbe.ProbeAsync(source, adapter, CancellationToken.None);
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    var holdSeconds = Math.Clamp(builder.Configuration.GetValue<int?>("CopartExcel:DiagnosticHoldSeconds") ?? 60, 30, 120);
    await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
    return;
}

if (args.Contains("--copart-media-enrich", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var processor = scope.ServiceProvider.GetRequiredService<ICopartMediaEnrichmentProcessor>();
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(await processor.RunAsync(CancellationToken.None)));
    return;
}

if (args.Contains("--copart-future-body-style-backfill", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var bodyStyleProcessor = scope.ServiceProvider.GetRequiredService<ICopartFutureBodyStyleBackfillProcessor>();
    var mediaProcessor = scope.ServiceProvider.GetRequiredService<ICopartMediaEnrichmentProcessor>();
    var bodyStyle = await bodyStyleProcessor.RunAsync(CancellationToken.None);
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { stage = "body-style", result = bodyStyle }));
    var media = await mediaProcessor.RunAsync(CancellationToken.None);
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { stage = "media", result = media }));
    if (!bodyStyle.Processed || bodyStyle.Failed > 0 || !media.Processed || media.Failed > 0) Environment.ExitCode = 1;
    return;
}

if (args.Contains("--copart-auction-history-backfill", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var processor = scope.ServiceProvider.GetRequiredService<ICopartAuctionHistoryBackfillProcessor>();
    var result = await processor.RunAsync(CancellationToken.None);
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    if (!result.Processed || result.Failed > 0) Environment.ExitCode = 1;
    return;
}

if (args.Contains("--copart-auction-history-report", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(await store.GetCopartAuctionHistoryReportAsync(CancellationToken.None)));
    return;
}

if (args.Contains("--copart-scoring-report", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(await store.GetCopartScoringCoverageReportAsync(CancellationToken.None)));
    return;
}

if (args.Contains("--seller-audit-report", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var store = scope.ServiceProvider.GetRequiredService<IInventorySnapshotStore>();
    var report = await store.GetSellerAuditReportAsync(CancellationToken.None);
    Console.WriteLine($"SELLER_AUDIT_TOTAL|{report.TotalVehicles}|{report.SaleDateFrom:O}");
    foreach (var row in report.Rows)
    {
        Console.WriteLine($"SELLER_AUDIT_ROW|{row.Platform}|{row.SellerName}|{row.SellerType}|{row.SellerClass}|{row.SellerTextClass}|{row.SellerCategory}|{row.ClassificationConfidence:0.00}|{row.NeedsReview}|{row.VehicleCount}");
    }
    return;
}

if (args.Contains("--copart-scoring-backfill", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<ICopartScoringBackfillProcessor>();
        var result = await processor.RunAsync(CancellationToken.None);
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        if (result.Failed > 0 || result.Remaining > 0) Environment.ExitCode = 1;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Copart scoring backfill fatal error: {exception}");
        Environment.ExitCode = 1;
    }

    return;
}

if (args.Contains("--copart-title-backfill", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<ICopartTitleBackfillProcessor>();
        var result = await processor.RunAsync(CancellationToken.None);
        Console.Error.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        if (!result.Processed || result.Failed > 0) Environment.ExitCode = 1;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"Copart title backfill fatal error: {exception}");
        Environment.ExitCode = 1;
    }

    var holdSeconds = Math.Clamp(builder.Configuration.GetValue<int?>("CopartExcel:DiagnosticHoldSeconds") ?? 60, 30, 120);
    await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
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
    var isExpectedNoOp = result.IsDuplicate || string.Equals(result.RejectionReason, "SKIPPED_LOCK_HELD: another Copart snapshot processor is active.", StringComparison.Ordinal);
    if (!isExpectedNoOp && (!result.Processed || !result.IsComplete || result.Errors > 0))
    {
        Environment.ExitCode = 1;
    }

    return;
}

if (args.Contains("--copart-excel-diagnostics", StringComparer.OrdinalIgnoreCase))
{
    try
    {
        await using var scope = app.Services.CreateAsyncScope();
        var processor = scope.ServiceProvider.GetRequiredService<ICopartExcelSnapshotProcessor>();
        var result = await processor.RunLatestAsync(CancellationToken.None);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
        Environment.ExitCode = (!result.Processed || !result.IsComplete || result.Errors > 0) ? 1 : 0;
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine(exception);
        Environment.ExitCode = 1;
    }

    var holdSeconds = Math.Clamp(builder.Configuration.GetValue<int?>("CopartExcel:DiagnosticHoldSeconds") ?? 300, 30, 600);
    await Task.Delay(TimeSpan.FromSeconds(holdSeconds));
    return;
}

if (args.Contains("--copart-excel-run", StringComparer.OrdinalIgnoreCase))
{
    await using var scope = app.Services.CreateAsyncScope();
    var processor = scope.ServiceProvider.GetRequiredService<ICopartExcelSnapshotProcessor>();
    var result = await processor.RunLatestAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    var isExpectedNoOp = result.IsDuplicate || string.Equals(result.RejectionReason, "SKIPPED_LOCK_HELD: another Copart snapshot processor is active.", StringComparison.Ordinal);
    if (!isExpectedNoOp && (!result.Processed || !result.IsComplete || result.Errors > 0))
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
