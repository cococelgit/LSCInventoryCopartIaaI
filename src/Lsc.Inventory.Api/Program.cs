using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Scoring;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Sources;
using Lsc.Inventory.Api.Storage;
using Lsc.Inventory.Api.Workers;
using Polly;
using System.Security.Cryptography;
using System.Text;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<ApibaraOptions>()
    .Bind(builder.Configuration.GetSection(ApibaraOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services
    .AddOptions<AuctionsApiOptions>()
    .Bind(builder.Configuration.GetSection(AuctionsApiOptions.SectionName))
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
    .AddOptions<IaaINationalOptions>()
    .Bind(builder.Configuration.GetSection(IaaINationalOptions.SectionName))
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
    .AddOptions<FacetsRedisOptions>()
    .Bind(builder.Configuration.GetSection(FacetsRedisOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services.AddSingleton<IFacetsV2SharedCache>(serviceProvider =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FacetsRedisOptions>>().Value;
    return options.IsConfigured
        ? new AzureManagedRedisFacetsV2SharedCache(
            serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<FacetsRedisOptions>>(),
            serviceProvider.GetRequiredService<ILogger<AzureManagedRedisFacetsV2SharedCache>>())
        : DisabledFacetsV2SharedCache.Instance;
});

builder.Services
    .AddOptions<BlobAuditOptions>()
    .Bind(builder.Configuration.GetSection(BlobAuditOptions.SectionName))
    .ValidateDataAnnotations();

builder.Services
    .AddOptions<ScoringOptions>()
    .Bind(builder.Configuration.GetSection(ScoringOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

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
builder.Services.AddHttpClient<IAuctionsApiClient, AuctionsApiClient>((serviceProvider, client) =>
{
    var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<AuctionsApiOptions>>().Value;
    client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
    client.Timeout = TimeSpan.FromSeconds(options.RequestTimeoutSeconds);
})
.AddResilienceHandler("auctions-api", pipeline =>
{
    pipeline.AddTimeout(TimeSpan.FromSeconds(30));
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
builder.Services.AddScoped<IIaaINationalSyncProcessor, IaaINationalSyncProcessor>();
builder.Services.AddScoped<ICanonicalInventoryIngestionPipeline, CanonicalInventoryIngestionPipeline>();
builder.Services.AddScoped<IAuctionsApiIncrementalSyncProcessor, AuctionsApiIncrementalSyncProcessor>();
builder.Services.AddScoped<ICopartExcelSnapshotAdapter, CopartExcelSnapshotAdapter>();
builder.Services.AddScoped<ICopartExcelSnapshotSource, CopartBlobSnapshotSource>();
builder.Services.AddScoped<ICopartExcelSnapshotProcessor, CopartExcelSnapshotProcessor>();
builder.Services.AddScoped<IInventoryScoringProcessor, InventoryScoringProcessor>();
builder.Services.AddSingleton<IInventorySearchProjectionRebuildRunner, InventorySearchProjectionRebuildRunner>();
builder.Services.AddSingleton<ISearchProjectionRebuildCoordinator, SearchProjectionRebuildCoordinator>();
builder.Services.AddHostedService<InventorySyncWorker>();
if (builder.Configuration.GetValue<bool>("SearchProjection:WarmupOnStartup"))
{
    builder.Services.AddHostedService<SearchProjectionWarmupWorker>();
}

var app = builder.Build();
var inventoryReadToken = builder.Configuration["InventoryApi:Token"] ?? Environment.GetEnvironmentVariable("INVENTORY_API_TOKEN");
var titleTaxonomyFacetsEnabled = builder.Configuration.GetValue("TitleTaxonomy:FacetsEnabled", false);

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

static PublicInventoryVehicle ToPublicVehicle(
    StoredVehicleSnapshot snapshot,
    Uri? requestBaseUri = null,
    string? mediaSigningToken = null,
    LscVehicleScoringResult? fullScoring = null,
    int? maxMediaItems = null)
{
    var publicMediaLimit = maxMediaItems.HasValue ? Math.Clamp(maxMediaItems.Value, 1, 100) : (int?)null;
    var vehicle = snapshot.Vehicle;
    var platform = vehicle.Platform?.Trim().ToLowerInvariant() ?? "unknown";
    var lot = vehicle.LotNumber ?? snapshot.Identity;
    var mediaCandidates = (vehicle.Media?.Items ?? Array.Empty<AuctionMediaItem>())
        .Select(item => new { Url = SafePhotoUrl(item.Large) ?? SafePhotoUrl(item.Thumb), item.Type })
        .Where(item => item.Url is not null)
        .Select(item => new PublicMediaItem(item.Url!, item.Type))
        .DistinctBy(item => item.Url);
    var media = (publicMediaLimit.HasValue ? mediaCandidates.Take(publicMediaLimit.Value) : mediaCandidates).ToArray();
    var photos = string.Equals(platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase) && requestBaseUri is not null && !string.IsNullOrWhiteSpace(mediaSigningToken)
        ? (publicMediaLimit.HasValue
            ? (vehicle.Media?.Photos ?? Array.Empty<string>())
                .Select((source, index) => new { source, index })
                .Where(item => IsApprovedCopartMediaUrl(item.source))
                .Take(publicMediaLimit.Value)
            : (vehicle.Media?.Photos ?? Array.Empty<string>())
            .Select((source, index) => new { source, index })
            .Where(item => IsApprovedCopartMediaUrl(item.source)))
            .Select(item =>
            {
                var expires = DateTimeOffset.UtcNow.AddHours(24).ToUnixTimeSeconds();
                var signature = CreateCopartMediaSignature(platform, lot, item.index, expires, mediaSigningToken!);
                return new Uri(requestBaseUri, $"/api/v1/inventory/media/{Uri.EscapeDataString(platform)}/{Uri.EscapeDataString(lot)}/{item.index}?expires={expires}&sig={Uri.EscapeDataString(signature)}").ToString();
            })
            .ToArray()
        : (publicMediaLimit.HasValue
            ? media.Select(item => item.Url)
                .Concat(vehicle.Media?.Photos ?? Array.Empty<string>())
                .Select(SafePhotoUrl)
                .Where(url => url is not null)
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(publicMediaLimit.Value)
            : media.Select(item => item.Url)
            .Concat(vehicle.Media?.Photos ?? Array.Empty<string>())
            .Select(SafePhotoUrl)
            .Where(url => url is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase))
            .ToArray();
    var publicMedia = string.Equals(platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase)
        ? photos.Select(url => new PublicMediaItem(url, "image")).ToArray()
        : media;
    var description = vehicle.Details?.VehicleDescription;
    var information = vehicle.Details?.VehicleInformation;
    var sale = vehicle.Details?.SaleInformation;
    var scoring = ToPublicScoring(snapshot.Scoring, fullScoring);
    var titleDescriptor = TitleFacetCategory.Describe(vehicle);
    var titleTaxonomy = ReadCopartTitleTaxonomy(platform, vehicle.AdditionalData);
    var runConditionRaw = string.Equals(platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase)
        ? vehicle.Condition?.RunCondition?.Label ?? vehicle.Condition?.RunCondition?.Value
        : null;
    var runCondition = string.Equals(platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase)
        ? NormalizePublicRunCondition(vehicle.Condition?.RunCondition?.Value ?? vehicle.Condition?.RunCondition?.Label)
        : null;
    var buyNowUsd = vehicle.Pricing?.BuyNowUsd is > 0m ? vehicle.Pricing.BuyNowUsd : null;
    return new PublicInventoryVehicle
    {
        Lot = lot,
        Vin = vehicle.Vin,
        Platform = platform,
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
        RunCondition = runCondition,
        RunConditionRaw = runConditionRaw,
        HasKey = vehicle.Condition?.HasKey,
        AuctionAt = vehicle.Auction?.AuctionAt,
        LotStatus = vehicle.Auction?.LotStatus,
        LotSubStatus = vehicle.Auction?.LotSubStatus,
        IsBuyNow = buyNowUsd is not null,
        IsTimed = vehicle.Auction?.IsTimed,
        CurrentBidUsd = vehicle.Pricing?.CurrentBidUsd,
        PreBidUsd = vehicle.Pricing?.PreBidUsd,
        BuyNowUsd = buyNowUsd,
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
        SellerType = SellerTaxonomy.Normalize(vehicle.Seller?.Type ?? sale?.SellerType),
        TitleType = vehicle.SaleDocument?.Name ?? (string.Equals(platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase) ? vehicle.Title : null),
        TitleCategory = titleTaxonomy.Category,
        TitleDisplayLabel = titleTaxonomy.DisplayLabel,
        TitleFlags = titleTaxonomy.Flags,
        TitleReviewStatus = titleTaxonomy.ReviewStatus,
        TitleTaxonomyVersion = titleTaxonomy.Version,
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
        Media = publicMedia,
        Scoring = scoring
    };
}

static PublicLscScoring? ToPublicScoring(LscScoringSummary? summary, LscVehicleScoringResult? full)
{
    if (summary is null && full is null) return null;
    var status = full?.Status ?? summary!.Status;
    var preGrade = full?.PreGrade ?? summary!.PreGrade;
    var buyScore = full?.BuyScore ?? summary!.BuyScore;
    var maxPointsEvaluable = full?.MaxPointsEvaluable ?? summary!.MaxPointsEvaluable;
    var coveragePercent = full?.CoveragePercent ?? summary!.CoveragePercent;
    var confidencePercent = full?.ConfidencePercent ?? summary!.ConfidencePercent;
    var category = full?.Category ?? summary!.Category;
    var policyVersion = full?.PolicyVersion ?? summary!.PolicyVersion;
    var scoredAt = full?.ScoredAt ?? summary!.ScoredAt;
    return new PublicLscScoring
    {
        Status = status,
        PreGrade = preGrade,
        BuyScore = buyScore,
        MaxPointsEvaluable = maxPointsEvaluable,
        CoveragePercent = coveragePercent,
        ConfidencePercent = confidencePercent,
        Category = category,
        PolicyVersion = policyVersion,
        ScoredAt = scoredAt,
        ReasonCodes = full?.ReasonCodes ?? [],
        MissingFields = full?.MissingFields ?? [],
        Factors = full?.Factors?.Select(factor => new PublicLscScoreFactor(factor.Code, factor.Name, factor.Points, factor.MaxPointsEvaluable, factor.Evaluated, factor.Explanation)).ToArray() ?? [],
        Penalties = full?.Penalties?.Select(penalty => new PublicLscScorePenalty(penalty.Code, penalty.Name, penalty.Points, penalty.Explanation)).ToArray() ?? []
    };
}

static (string? Category, string? DisplayLabel, IReadOnlyList<string> Flags, string? ReviewStatus, string? Version) ReadCopartTitleTaxonomy(string platform, Dictionary<string, JsonElement>? additionalData)
{
    if (!string.Equals(platform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase) || additionalData is null)
        return (null, null, [], null, null);

    static string? ReadString(Dictionary<string, JsonElement> data, string key)
        => data.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    static IReadOnlyList<string> ReadFlags(Dictionary<string, JsonElement> data)
    {
        if (!data.TryGetValue("title_flags", out var value) || value.ValueKind != JsonValueKind.Array) return [];
        return value.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    var category = ReadString(additionalData, "title_category");
    var labels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CLEAN"] = "Título limpio",
        ["BRANDED_TITLE"] = "Título con marca",
        ["SALVAGE"] = "Salvage / Salvamento",
        ["REBUILT_RECONSTRUCTED"] = "Reconstruido / Rebuilt",
        ["NON_REPAIRABLE_PARTS_SCRAP"] = "No reparable / piezas / chatarra",
        ["EXPORT_ONLY"] = "Solo exportación",
        ["DOCUMENT_ONLY"] = "Documento especial",
        ["STATE_VARIANT_VERIFY"] = "Variante estatal — verificar",
        ["OTHER_UNVERIFIED"] = "Tipo de título por verificar"
    };
    return (category, category is not null && labels.TryGetValue(category, out var label) ? label : null, ReadFlags(additionalData), ReadString(additionalData, "title_review_status"), ReadString(additionalData, "title_taxonomy_version"));
}

static string? NormalizePublicRunCondition(string? value)
{
    if (string.IsNullOrWhiteSpace(value)) return "UNVERIFIED";
    var normalized = value.Trim().ToUpperInvariant().Replace("&", " AND ", StringComparison.Ordinal);
    if (normalized.Contains("RUNS AND DRIVES", StringComparison.Ordinal)) return "RUNS_AND_DRIVES";
    if (normalized.Contains("START", StringComparison.Ordinal)) return "STARTS";
    if (normalized.Contains("STATIONARY", StringComparison.Ordinal)) return "STATIONARY";
    if (normalized.Contains("NO INFORMATION", StringComparison.Ordinal)) return "UNVERIFIED";
    return "UNVERIFIED";
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

app.MapGet("/api/v1/inventory/title-taxonomy/status", async (HttpContext context, IInventorySnapshotStore store, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    return Results.Ok(await store.GetCopartTitleTaxonomyCoverageAsync(cancellationToken));
});

app.MapGet("/api/v1/inventory/internal/facets-v2/cache-status", (HttpContext context, IFacetsV2SharedCache sharedCache) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    // Intentionally excludes endpoint, identity IDs, keys, token contents, Redis commands and cached payloads.
    return Results.Ok(sharedCache.GetDiagnostics());
});

app.MapGet("/api/v1/inventory/seller-taxonomy/audit", async (HttpContext context, IInventorySnapshotStore store, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    return Results.Ok(await store.GetSellerTaxonomyAuditAsync(cancellationToken));
});

app.MapGet("/api/v1/inventory/recent", async (HttpContext context, IInventorySnapshotStore store, int? take, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var limit = Math.Clamp(take ?? 1000, 1, 1000);
    var snapshots = (await store.GetRecentAsync(5000, cancellationToken))
        .Where(snapshot => AuctionEligibilityEvaluator.Evaluate(snapshot.Vehicle).LoadToSystem)
        .Take(limit)
        .ToArray();
    var requestBaseUri = PublicRequestUriResolver.Resolve(context.Request);
    var vehicles = snapshots.Select(snapshot => ToPublicVehicle(snapshot, requestBaseUri, inventoryReadToken)).ToArray();
    var generatedAt = snapshots.Length > 0 ? snapshots.Max(snapshot => snapshot.ObservedAt) : DateTimeOffset.UtcNow;
    return Results.Ok(new PublicInventoryResponse("lsc-inventory-postgres", generatedAt, vehicles));
});

app.MapGet("/api/v1/inventory/search", async (
    HttpContext context,
    IInventorySnapshotStore store,
    int? page,
    int? pageSize,
    string? query,
    string? platform,
    string? sort,
    string[]? makes,
    string[]? models,
    string[]? vehicleTypes,
    string[]? titles,
    string[]? titleCategories,
    bool? excludeSpecialTitles,
    string[]? states,
    string[]? facilities,
    string[]? primaryDamages,
    string[]? secondaryDamages,
    string[]? sellerTypes,
    string[]? engineLayouts,
    string[]? cylinders,
    int? yearFrom,
    int? yearTo,
    decimal? odometerFrom,
    decimal? odometerTo,
    decimal? priceFrom,
    decimal? priceTo,
    decimal? buyNowFrom,
    decimal? buyNowTo,
    decimal? maxBid,
    DateTimeOffset? auctionFrom,
    DateTimeOffset? auctionTo,
    bool? buyNowOnly,
    string[]? transmissions,
    string[]? fuels,
    string[]? drives,
    string[]? bodyStyles,
    string[]? colors,
    string[]? lossTypes,
    string[]? startCodes,
    string[]? runConditions,
    bool? withPhotosOnly,
    string? auctionStatus,
    bool? withBidOnly,
    string? keyMode,
    decimal? providerEstimateFrom,
    decimal? providerEstimateTo,
    decimal? engineSizeFrom,
    decimal? engineSizeTo,
    decimal? horsepowerFrom,
    decimal? horsepowerTo,
    bool? listView,
    CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    if (titleCategories is { Length: > 0 })
    {
        if (!titleTaxonomyFacetsEnabled)
        {
            return Results.Problem("La taxonomía normalizada de títulos Copart aún no está habilitada; falta validar la cobertura del backfill.", statusCode: StatusCodes.Status503ServiceUnavailable, title: "Taxonomía de títulos pendiente de validación");
        }
        var coverage = await store.GetCopartTitleTaxonomyCoverageAsync(cancellationToken);
        if (!coverage.GateEligible)
        {
            return Results.Problem($"La cobertura validada de títulos Copart es {coverage.CoveragePercent:0.##}%; se requiere al menos 95%.", statusCode: StatusCodes.Status503ServiceUnavailable, title: "Cobertura de taxonomía insuficiente");
        }
    }
    static string[]? Normalize(string[]? values) => values?.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var result = await store.SearchAsync(new InventorySearchRequest(
        page ?? 1,
        pageSize ?? 24,
        query,
        platform,
        sort,
        Normalize(makes),
        Normalize(models),
        Normalize(vehicleTypes),
        Normalize(titles),
        Normalize(states),
        Normalize(facilities),
        Normalize(primaryDamages),
        Normalize(secondaryDamages),
        Normalize(sellerTypes),
        Normalize(engineLayouts),
        Normalize(cylinders),
        yearFrom,
        yearTo,
        odometerFrom,
        odometerTo,
        priceFrom,
        priceTo,
        auctionFrom,
        auctionTo,
        buyNowOnly,
        Normalize(transmissions),
        Normalize(fuels),
        Normalize(drives),
        Normalize(bodyStyles),
        Normalize(colors),
        Normalize(lossTypes),
        Normalize(startCodes),
        Normalize(runConditions),
        withPhotosOnly,
        auctionStatus,
        withBidOnly,
        keyMode,
        providerEstimateFrom,
        providerEstimateTo,
        engineSizeFrom,
        engineSizeTo,
        horsepowerFrom,
        horsepowerTo,
        maxBid,
        excludeSpecialTitles == true,
        TitleCategories: Normalize(titleCategories),
        BuyNowFrom: buyNowFrom,
        BuyNowTo: buyNowTo), cancellationToken);
    return Results.Ok(new PublicInventorySearchResponse(
        "lsc-inventory-postgres",
        result.GeneratedAt,
        result.Page,
        result.PageSize,
        result.Total,
        result.Items.Select(snapshot => ToPublicVehicle(
            snapshot,
            PublicRequestUriResolver.Resolve(context.Request),
            inventoryReadToken,
            maxMediaItems: listView == true ? 1 : null)).ToArray()));
});

app.MapGet("/api/v1/inventory/summary", async (HttpContext context, IInventorySnapshotStore store, string[]? makes, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var normalizedMakes = makes?.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    var summary = await store.GetInventorySearchSummaryAsync(new InventorySearchRequest(1, 1, Makes: normalizedMakes), cancellationToken);
    return Results.Ok(new PublicInventorySummaryResponse(
        "lsc-inventory-postgres",
        summary.GeneratedAt,
        summary.Total,
        summary.Facets));
});

app.MapGet("/api/v1/inventory/facets-v2", async (
    HttpContext context,
    IInventorySnapshotStore store,
    string[]? requestedFacets,
    string? query,
    string? platform,
    string[]? makes,
    string[]? models,
    string[]? vehicleTypes,
    string[]? titles,
    string[]? titleCategories,
    bool? excludeSpecialTitles,
    string[]? states,
    string[]? facilities,
    string[]? primaryDamages,
    string[]? secondaryDamages,
    string[]? sellerTypes,
    string[]? engineLayouts,
    string[]? cylinders,
    int? yearFrom,
    int? yearTo,
    decimal? odometerFrom,
    decimal? odometerTo,
    decimal? priceFrom,
    decimal? priceTo,
    decimal? buyNowFrom,
    decimal? buyNowTo,
    decimal? maxBid,
    DateTimeOffset? auctionFrom,
    DateTimeOffset? auctionTo,
    bool? buyNowOnly,
    string[]? transmissions,
    string[]? fuels,
    string[]? drives,
    string[]? bodyStyles,
    string[]? colors,
    string[]? lossTypes,
    string[]? startCodes,
    string[]? runConditions,
    bool? withPhotosOnly,
    string? auctionStatus,
    bool? withBidOnly,
    string? keyMode,
    decimal? providerEstimateFrom,
    decimal? providerEstimateTo,
    decimal? engineSizeFrom,
    decimal? engineSizeTo,
    decimal? horsepowerFrom,
    decimal? horsepowerTo,
    decimal? preGradeFrom,
    string[]? scoringStatuses,
    CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    if (titleCategories is { Length: > 0 })
    {
        if (!titleTaxonomyFacetsEnabled)
            return Results.Problem("La taxonomía normalizada de títulos Copart aún no está habilitada; falta validar la cobertura del backfill.", statusCode: StatusCodes.Status503ServiceUnavailable, title: "Taxonomía de títulos pendiente de validación");
        var coverage = await store.GetCopartTitleTaxonomyCoverageAsync(cancellationToken);
        if (!coverage.GateEligible)
            return Results.Problem($"La cobertura validada de títulos Copart es {coverage.CoveragePercent:0.##}%; se requiere al menos 95%.", statusCode: StatusCodes.Status503ServiceUnavailable, title: "Cobertura de taxonomía insuficiente");
    }

    static string[]? NormalizeFacetValues(string[]? values) => values?
        .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    try
    {
        var filters = new InventorySearchRequest(
            1,
            1,
            query,
            platform,
            null,
            NormalizeFacetValues(makes),
            NormalizeFacetValues(models),
            NormalizeFacetValues(vehicleTypes),
            NormalizeFacetValues(titles),
            NormalizeFacetValues(states),
            NormalizeFacetValues(facilities),
            NormalizeFacetValues(primaryDamages),
            NormalizeFacetValues(secondaryDamages),
            NormalizeFacetValues(sellerTypes),
            NormalizeFacetValues(engineLayouts),
            NormalizeFacetValues(cylinders),
            yearFrom,
            yearTo,
            odometerFrom,
            odometerTo,
            priceFrom,
            priceTo,
            auctionFrom,
            auctionTo,
            buyNowOnly,
            NormalizeFacetValues(transmissions),
            NormalizeFacetValues(fuels),
            NormalizeFacetValues(drives),
            NormalizeFacetValues(bodyStyles),
            NormalizeFacetValues(colors),
            NormalizeFacetValues(lossTypes),
            NormalizeFacetValues(startCodes),
            NormalizeFacetValues(runConditions),
            withPhotosOnly,
            auctionStatus,
            withBidOnly,
            keyMode,
            providerEstimateFrom,
            providerEstimateTo,
            engineSizeFrom,
            engineSizeTo,
            horsepowerFrom,
            horsepowerTo,
            maxBid,
            excludeSpecialTitles == true,
            preGradeFrom,
            NormalizeFacetValues(scoringStatuses),
            NormalizeFacetValues(titleCategories),
            BuyNowFrom: buyNowFrom,
            BuyNowTo: buyNowTo);
        var response = await store.GetInventoryFacetsV2Async(
            new InventoryFacetsV2Request(filters, NormalizeFacetValues(requestedFacets)),
            cancellationToken);
        return Results.Ok(response);
    }
    catch (ArgumentException exception)
    {
        return Results.BadRequest(new { error = exception.Message });
    }
    catch (InvalidOperationException exception)
    {
        return Results.Problem(exception.Message, statusCode: StatusCodes.Status503ServiceUnavailable, title: "Facets V2 no disponible");
    }
});

app.MapGet("/api/v1/inventory/vehicle/{lot}", async (HttpContext context, IInventorySnapshotStore store, ILoggerFactory loggerFactory, string lot, string? platform, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var normalizedPlatform = platform?.Trim().ToLowerInvariant();
    if (normalizedPlatform is not null &&
        !string.Equals(normalizedPlatform, InventorySourcePolicy.CopartExcelSource, StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(normalizedPlatform, InventorySourcePolicy.IaaIApibaraSource, StringComparison.OrdinalIgnoreCase))
    {
        return Results.BadRequest(new { error = "Unsupported platform." });
    }

    var snapshot = normalizedPlatform is null
        ? await store.GetByLotAsync(lot, cancellationToken)
        : await store.GetByPlatformAndLotAsync(normalizedPlatform, lot, cancellationToken);
    // Search and detail share lifecycle-active rows. Re-evaluating an historical snapshot
    // here against the current date can hide a lot that is still intentionally visible.
    if (snapshot is null)
        return Results.NotFound();
    LscVehicleScoringResult? scoring = null;
    try
    {
        scoring = await store.GetScoreByLotAsync(lot, cancellationToken);
    }
    catch (Exception exception)
    {
        loggerFactory.CreateLogger("InventoryVehicleDetail")
            .LogWarning(exception,
                "Serving active vehicle detail without full scoring after scoring lookup failed. Platform: {Platform}; Lot: {Lot}.",
                normalizedPlatform ?? "any",
                lot);
    }
    try
    {
        return Results.Ok(ToPublicVehicle(snapshot, PublicRequestUriResolver.Resolve(context.Request), inventoryReadToken, scoring));
    }
    catch (Exception exception)
    {
        loggerFactory.CreateLogger("InventoryVehicleDetail")
            .LogWarning(exception,
                "Serving active vehicle detail without full scoring after public scoring mapping failed. Platform: {Platform}; Lot: {Lot}.",
                normalizedPlatform ?? "any",
                lot);
        return Results.Ok(ToPublicVehicle(snapshot, PublicRequestUriResolver.Resolve(context.Request), inventoryReadToken));
    }
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
    if (snapshot is null) return Results.NotFound();
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

app.MapGet("/internal/iaai-national/status", async (HttpContext context, IInventorySnapshotStore store, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var status = await store.GetNationalSyncOperationalStatusAsync("iaai-national-open", cancellationToken);
    return Results.Ok(status);
});

app.MapGet("/internal/executions", async (HttpContext context, IInventorySnapshotStore store, int? page, int? pageSize, string? platform, string? status, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    return Results.Ok(await store.GetExecutionHistoryAsync(new InventoryExecutionHistoryRequest(page ?? 1, pageSize ?? 25, platform, status), cancellationToken));
});

app.MapGet("/internal/executions/{runId:guid}/events", async (HttpContext context, IInventorySnapshotStore store, Guid runId, int? page, int? pageSize, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    return Results.Ok(await store.GetExecutionEventsAsync(runId, page ?? 1, pageSize ?? 50, cancellationToken));
});

app.MapPost("/internal/search-projection/rebuild", (HttpContext context, ISearchProjectionRebuildCoordinator coordinator) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var request = coordinator.RequestRebuild();
    return request.Accepted
        ? Results.Accepted("/internal/search-projection/status", request)
        : Results.Conflict(request);
});

app.MapGet("/internal/search-projection/status", async (HttpContext context, IInventorySnapshotStore store, ISearchProjectionRebuildCoordinator coordinator, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var projection = await store.GetSearchProjectionStatusAsync(cancellationToken);
    return Results.Ok(new
    {
        projection.Ready,
        projection.Rows,
        projection.GeneratedAt,
        projection.FacetsRefreshedAt,
        projection.Duration,
        rebuild = coordinator.GetStatus()
    });
});

app.MapGet("/internal/scoring/status", async (HttpContext context, IInventorySnapshotStore store, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    return Results.Ok(await store.GetScoringOperationalStatusAsync(cancellationToken));
});

app.MapGet("/internal/scoring/runs", async (HttpContext context, IInventorySnapshotStore store, int? take, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    return Results.Ok(await store.GetRecentScoringRunsAsync(take ?? 20, cancellationToken));
});

app.MapPost("/internal/scoring/backfill", async (HttpContext context, IInventoryScoringProcessor processor, int? maximum, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    return Results.Ok(await processor.RunBackfillAsync(maximum, cancellationToken, "manual-api"));
});

app.MapPost("/internal/scoring/process", async (HttpContext context, IInventoryScoringProcessor processor, int? maximum, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    return Results.Ok(await processor.ProcessBatchAsync(maximum, cancellationToken));
});

app.MapGet("/internal/scoring/vehicle/{lot}", async (HttpContext context, IInventorySnapshotStore store, string lot, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    var result = await store.GetScoreByLotAsync(lot, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
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

app.MapPost("/internal/auctions-api/incremental", async (HttpContext context, IAuctionsApiIncrementalSyncProcessor processor, string? platform, bool? persist, CancellationToken cancellationToken) =>
{
    if (!HasValidReadToken(context, inventoryReadToken)) return Results.Unauthorized();
    // A valid token and AuctionsApi:Enabled are not enough for canonical writes.
    // The processor applies the second AllowWrites gate when persist=true.
    var result = await processor.RunAsync(platform ?? "", persist == true, cancellationToken);
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

var iaaiStartupMode = IaaIStartupModeResolver.Resolve(args, builder.Configuration);
if (iaaiStartupMode == IaaIStartupMode.Pilot)
{
    await using var scope = app.Services.CreateAsyncScope();
    var processor = scope.ServiceProvider.GetRequiredService<IIaaIPilotProcessor>();
    var result = await processor.RunAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    if (result.Failures.Count > 0 || result.Observed == 0) Environment.ExitCode = 1;
    return;
}

if (iaaiStartupMode == IaaIStartupMode.National)
{
    await using var scope = app.Services.CreateAsyncScope();
    var processor = scope.ServiceProvider.GetRequiredService<IIaaINationalSyncProcessor>();
    var result = await processor.RunAsync(CancellationToken.None);
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    if (result.ShouldRetry && !result.Skipped) Environment.ExitCode = 1;
    return;
}

if (args.Contains("--scoring-backfill", StringComparer.OrdinalIgnoreCase)
    || builder.Configuration.GetValue<bool>("Scoring:RunOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var processor = scope.ServiceProvider.GetRequiredService<IInventoryScoringProcessor>();
    var result = await processor.RunBackfillAsync(null, CancellationToken.None, "scheduled-azure-job");
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result));
    if (result.Failed > 0) Environment.ExitCode = 1;
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
