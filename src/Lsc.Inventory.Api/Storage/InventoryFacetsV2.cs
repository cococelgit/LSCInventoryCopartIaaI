using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Lsc.Inventory.Api.Storage;

public static class InventoryFacetsV2Groups
{
    public const string Platforms = "platforms";
    public const string SellerTypes = "sellerTypes";
    public const string Makes = "makes";
    public const string Models = "models";
    public const string VehicleTypes = "vehicleTypes";
    public const string Titles = "titles";
    public const string States = "states";
    public const string Facilities = "facilities";
    public const string PrimaryDamages = "primaryDamages";
    public const string SecondaryDamages = "secondaryDamages";
    public const string EngineLayouts = "engineLayouts";
    public const string Cylinders = "cylinders";
    public const string Transmissions = "transmissions";
    public const string Fuels = "fuels";
    public const string Drives = "drives";
    public const string BodyStyles = "bodyStyles";
    public const string Colors = "colors";
    public const string LossTypes = "lossTypes";
    public const string StartCodes = "startCodes";
    public const string RunConditions = "runConditions";
    public const string ScoringStatuses = "scoringStatuses";
    public const string Year = "year";
    public const string Odometer = "odometer";
    public const string CurrentBid = "currentBid";
    public const string ProviderEstimate = "providerEstimate";
    public const string AuctionDate = "auctionDate";
    public const string EngineSize = "engineSize";
    public const string Horsepower = "horsepower";
    public const string PreGrade = "preGrade";

    public static readonly IReadOnlyList<string> Core =
    [
        Platforms,
        SellerTypes,
        Makes,
        VehicleTypes,
        Titles,
        States,
        Transmissions,
        Fuels,
        Drives
    ];

    public static readonly IReadOnlySet<string> Categorical = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Platforms, SellerTypes, Makes, Models, VehicleTypes, Titles, States, Facilities,
        PrimaryDamages, SecondaryDamages, EngineLayouts, Cylinders, Transmissions, Fuels,
        Drives, BodyStyles, Colors, LossTypes, StartCodes, RunConditions, ScoringStatuses
    };

    public static readonly IReadOnlySet<string> NumericRanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Year, Odometer, CurrentBid, ProviderEstimate, EngineSize, Horsepower, PreGrade
    };

    public static readonly IReadOnlySet<string> DateRanges = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        AuctionDate
    };

    public static IReadOnlyList<string> NormalizeRequested(IReadOnlyCollection<string>? requested)
    {
        var source = requested is null ? Core : requested;
        var normalized = source
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var unsupported = normalized
            .Where(value => !Categorical.Contains(value) && !NumericRanges.Contains(value) && !DateRanges.Contains(value))
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unsupported.Length > 0)
            throw new ArgumentException($"Unsupported facet groups: {string.Join(", ", unsupported)}", nameof(requested));
        if (normalized.Length > 24)
            throw new ArgumentException("A Facets V2 request may include at most 24 groups; request high-cardinality groups on demand.", nameof(requested));

        return normalized;
    }
}

public static class InventoryFacetsV2Fingerprint
{
    public static string Create(InventoryFacetsV2Request request)
    {
        var filters = request.Filters;
        var builder = new StringBuilder(1024);
        AppendScalar(builder, "query", Normalize(filters.Query));
        AppendScalar(builder, "platform", Normalize(filters.Platform));
        AppendArray(builder, "makes", filters.Makes);
        AppendArray(builder, "models", filters.Models);
        AppendArray(builder, "vehicleTypes", filters.VehicleTypes);
        AppendArray(builder, "titles", Merge(filters.Titles, filters.TitleCategories));
        AppendArray(builder, "states", filters.States);
        AppendArray(builder, "facilities", filters.Facilities);
        AppendArray(builder, "primaryDamages", filters.PrimaryDamages);
        AppendArray(builder, "secondaryDamages", filters.SecondaryDamages);
        AppendArray(builder, "sellerTypes", filters.SellerTypes);
        AppendArray(builder, "engineLayouts", filters.EngineLayouts);
        AppendArray(builder, "cylinders", filters.Cylinders);
        AppendArray(builder, "transmissions", filters.Transmissions);
        AppendArray(builder, "fuels", filters.Fuels);
        AppendArray(builder, "drives", filters.Drives);
        AppendArray(builder, "bodyStyles", filters.BodyStyles);
        AppendArray(builder, "colors", filters.Colors);
        AppendArray(builder, "lossTypes", filters.LossTypes);
        AppendArray(builder, "startCodes", filters.StartCodes);
        AppendArray(builder, "runConditions", filters.RunConditions);
        AppendArray(builder, "scoringStatuses", filters.ScoringStatuses);
        AppendScalar(builder, "yearFrom", Format(filters.YearFrom));
        AppendScalar(builder, "yearTo", Format(filters.YearTo));
        AppendScalar(builder, "odometerFrom", Format(filters.OdometerFrom));
        AppendScalar(builder, "odometerTo", Format(filters.OdometerTo));
        AppendScalar(builder, "priceFrom", Format(filters.PriceFrom));
        AppendScalar(builder, "priceTo", Format(filters.PriceTo));
        AppendScalar(builder, "maxCurrentBid", Format(filters.MaxCurrentBid));
        AppendScalar(builder, "auctionFrom", Format(filters.AuctionFrom));
        AppendScalar(builder, "auctionTo", Format(filters.AuctionTo));
        AppendScalar(builder, "buyNowOnly", Format(filters.BuyNowOnly));
        AppendScalar(builder, "withPhotosOnly", Format(filters.WithPhotosOnly));
        AppendScalar(builder, "auctionStatus", Normalize(filters.AuctionStatus));
        AppendScalar(builder, "withBidOnly", Format(filters.WithBidOnly));
        AppendScalar(builder, "keyMode", Normalize(filters.KeyMode));
        AppendScalar(builder, "providerEstimateFrom", Format(filters.ProviderEstimateFrom));
        AppendScalar(builder, "providerEstimateTo", Format(filters.ProviderEstimateTo));
        AppendScalar(builder, "engineSizeFrom", Format(filters.EngineSizeFrom));
        AppendScalar(builder, "engineSizeTo", Format(filters.EngineSizeTo));
        AppendScalar(builder, "horsepowerFrom", Format(filters.HorsepowerFrom));
        AppendScalar(builder, "horsepowerTo", Format(filters.HorsepowerTo));
        AppendScalar(builder, "excludeSpecialTitles", Format(filters.ExcludeSpecialTitles));
        AppendScalar(builder, "preGradeFrom", Format(filters.PreGradeFrom));
        AppendArray(builder, "requestedFacets", InventoryFacetsV2Groups.NormalizeRequested(request.RequestedFacets));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    public static IReadOnlyCollection<string>? Merge(IReadOnlyCollection<string>? first, IReadOnlyCollection<string>? second)
    {
        if (first is null && second is null) return null;
        return (first ?? [])
            .Concat(second ?? [])
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AppendScalar(StringBuilder builder, string key, string? value) =>
        builder.Append(key).Append('=').Append(value ?? "<null>").Append('\n');

    private static void AppendArray(StringBuilder builder, string key, IReadOnlyCollection<string>? values)
    {
        if (values is null)
        {
            AppendScalar(builder, key, null);
            return;
        }

        var normalized = values
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
        AppendScalar(builder, key, $"[{string.Join(',', normalized)}]");
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
    private static string? Format<T>(T? value) where T : struct, IFormattable => value?.ToString(null, CultureInfo.InvariantCulture);
    private static string? Format(DateTimeOffset? value) => value?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string? Format(bool? value) => value?.ToString(CultureInfo.InvariantCulture).ToLowerInvariant();
}

public static class InventoryFacetsV2Selections
{
    public static IEnumerable<string> Get(InventorySearchRequest request, string group)
    {
        IReadOnlyCollection<string>? selected = group switch
        {
            InventoryFacetsV2Groups.Platforms => string.IsNullOrWhiteSpace(request.Platform) ? null : [request.Platform],
            InventoryFacetsV2Groups.Makes => request.Makes,
            InventoryFacetsV2Groups.Models => request.Models,
            InventoryFacetsV2Groups.VehicleTypes => request.VehicleTypes,
            InventoryFacetsV2Groups.Titles => InventoryFacetsV2Fingerprint.Merge(request.Titles, request.TitleCategories),
            InventoryFacetsV2Groups.States => request.States,
            InventoryFacetsV2Groups.Facilities => request.Facilities,
            InventoryFacetsV2Groups.PrimaryDamages => request.PrimaryDamages,
            InventoryFacetsV2Groups.SecondaryDamages => request.SecondaryDamages,
            InventoryFacetsV2Groups.SellerTypes => request.SellerTypes,
            InventoryFacetsV2Groups.EngineLayouts => request.EngineLayouts,
            InventoryFacetsV2Groups.Cylinders => request.Cylinders,
            InventoryFacetsV2Groups.Transmissions => request.Transmissions,
            InventoryFacetsV2Groups.Fuels => request.Fuels,
            InventoryFacetsV2Groups.Drives => request.Drives,
            InventoryFacetsV2Groups.BodyStyles => request.BodyStyles,
            InventoryFacetsV2Groups.Colors => request.Colors,
            InventoryFacetsV2Groups.LossTypes => request.LossTypes,
            InventoryFacetsV2Groups.StartCodes => request.StartCodes,
            InventoryFacetsV2Groups.RunConditions => request.RunConditions,
            InventoryFacetsV2Groups.ScoringStatuses => request.ScoringStatuses,
            _ => null
        };
        return selected?.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value.Trim()).Distinct(StringComparer.OrdinalIgnoreCase) ?? [];
    }
}
