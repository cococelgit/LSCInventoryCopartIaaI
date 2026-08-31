using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using System.Reflection;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class InventoryFacetsV2Tests
{
    [Fact]
    public void Default_request_uses_only_core_low_cardinality_groups()
    {
        var groups = InventoryFacetsV2Groups.NormalizeRequested(null);

        Assert.Contains(InventoryFacetsV2Groups.SellerTypes, groups);
        Assert.Contains(InventoryFacetsV2Groups.Makes, groups);
        Assert.DoesNotContain(InventoryFacetsV2Groups.Models, groups);
        Assert.DoesNotContain(InventoryFacetsV2Groups.Facilities, groups);
        Assert.DoesNotContain(InventoryFacetsV2Groups.RunConditions, groups);
        Assert.True(groups.Count <= 10);
    }

    [Fact]
    public void Request_rejects_unknown_groups_and_caps_fanout()
    {
        Assert.Throws<ArgumentException>(() => InventoryFacetsV2Groups.NormalizeRequested(["dropTable"]));
        Assert.Throws<ArgumentException>(() => InventoryFacetsV2Groups.NormalizeRequested(
            InventoryFacetsV2Groups.Categorical.Concat(InventoryFacetsV2Groups.NumericRanges).Take(25).ToArray()));
    }

    [Fact]
    public void Fingerprint_is_case_order_page_and_sort_independent_but_keeps_requested_groups()
    {
        var first = new InventoryFacetsV2Request(
            new InventorySearchRequest(1, 24, Sort: "pregrade-desc", Makes: ["Toyota", "FORD"], SellerTypes: ["Insurance"]),
            [InventoryFacetsV2Groups.SellerTypes, InventoryFacetsV2Groups.Makes]);
        var equivalent = new InventoryFacetsV2Request(
            new InventorySearchRequest(9, 100, Sort: "year-asc", Makes: ["ford", "TOYOTA"], SellerTypes: ["insurance"]),
            [InventoryFacetsV2Groups.Makes, InventoryFacetsV2Groups.SellerTypes]);
        var differentGroups = equivalent with { RequestedFacets = [InventoryFacetsV2Groups.Makes] };

        Assert.Equal(InventoryFacetsV2Fingerprint.Create(first), InventoryFacetsV2Fingerprint.Create(equivalent));
        Assert.NotEqual(InventoryFacetsV2Fingerprint.Create(first), InventoryFacetsV2Fingerprint.Create(differentGroups));
    }

    [Fact]
    public async Task Counts_are_and_between_groups_or_inside_group_and_self_excluding()
    {
        var store = await CreateStoreAsync();
        var request = new InventoryFacetsV2Request(
            new InventorySearchRequest(1, 24, Makes: ["Toyota"], SellerTypes: [SellerTaxonomy.Insurance]),
            [InventoryFacetsV2Groups.SellerTypes, InventoryFacetsV2Groups.Makes, InventoryFacetsV2Groups.Models]);

        var result = await store.GetInventoryFacetsV2Async(request, CancellationToken.None);

        Assert.Equal(1, result.Total);
        Assert.Equal(1, Count(result, InventoryFacetsV2Groups.SellerTypes, SellerTaxonomy.Insurance));
        Assert.Equal(1, Count(result, InventoryFacetsV2Groups.SellerTypes, SellerTaxonomy.Dealer));
        Assert.Equal(1, Count(result, InventoryFacetsV2Groups.Makes, "Toyota"));
        Assert.Equal(1, Count(result, InventoryFacetsV2Groups.Makes, "Ford"));
        Assert.Equal(1, Count(result, InventoryFacetsV2Groups.Models, "Camry"));
        Assert.Equal(0, Count(result, InventoryFacetsV2Groups.Models, "Corolla"));

        var multiSelect = await store.GetInventoryFacetsV2Async(
            new InventoryFacetsV2Request(
                request.Filters with { Makes = ["Toyota", "Ford"] },
                [InventoryFacetsV2Groups.Makes]),
            CancellationToken.None);
        Assert.Equal(2, multiSelect.Total);
    }

    [Fact]
    public async Task Active_selection_remains_visible_with_zero_and_ranges_exclude_their_own_group()
    {
        var store = await CreateStoreAsync();
        var zero = await store.GetInventoryFacetsV2Async(
            new InventoryFacetsV2Request(
                new InventorySearchRequest(1, 24, Makes: ["Toyota"], SellerTypes: [SellerTaxonomy.Government]),
                [InventoryFacetsV2Groups.SellerTypes, InventoryFacetsV2Groups.Makes]),
            CancellationToken.None);

        Assert.Equal(0, zero.Total);
        Assert.Equal(0, Count(zero, InventoryFacetsV2Groups.SellerTypes, SellerTaxonomy.Government));
        Assert.Equal(1, Count(zero, InventoryFacetsV2Groups.SellerTypes, SellerTaxonomy.Insurance));
        Assert.Equal(1, Count(zero, InventoryFacetsV2Groups.SellerTypes, SellerTaxonomy.Dealer));
        Assert.Equal(0, Count(zero, InventoryFacetsV2Groups.Makes, "Toyota"));

        var range = await store.GetInventoryFacetsV2Async(
            new InventoryFacetsV2Request(
                new InventorySearchRequest(1, 24, SellerTypes: [SellerTaxonomy.Insurance], YearFrom: 2021),
                [InventoryFacetsV2Groups.Year]),
            CancellationToken.None);
        Assert.Equal(0, range.Total);
        Assert.Equal(2019m, range.Ranges.Year?.Min);
        Assert.Equal(2020m, range.Ranges.Year?.Max);
    }

    [Fact]
    public void PostgreSql_contract_is_active_only_compact_parameterized_and_single_statement()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.FacetsV2.cs"));
        var normalized = source.ToLowerInvariant();

        Assert.Contains("with base as materialized", normalized);
        Assert.Contains("where {string.join(\" and \", fixedwhere)}", normalized);
        Assert.Contains("latest.is_active", normalized);
        Assert.Contains("union all", normalized);
        Assert.Contains("any(@{spec.parameter})", normalized);
        Assert.DoesNotContain("latest.payload", normalized);
        Assert.DoesNotContain("::json", normalized);
        Assert.DoesNotContain("media_has_360", normalized);
        Assert.DoesNotContain("payload::text", normalized);
        Assert.Contains("facetsv2cachemaximumentries = 128", normalized);
        Assert.Contains("timespan.fromseconds(15)", normalized);
        Assert.Contains("sourceversion", normalized);
        Assert.Contains("concurrentdictionary", normalized);
    }

    [Fact]
    public void Generated_postgresql_uses_fixed_projection_columns_parameters_and_self_exclusion()
    {
        var store = new PostgresSnapshotStore(
            Microsoft.Extensions.Options.Options.Create(new PersistenceOptions()),
            Microsoft.Extensions.Options.Options.Create(new BlobAuditOptions()),
            NullLogger<PostgresSnapshotStore>.Instance);
        var method = typeof(PostgresSnapshotStore).GetMethod("BuildFacetsV2Command", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);
        using var connection = new NpgsqlConnection();
        using var command = Assert.IsType<NpgsqlCommand>(method!.Invoke(store,
        [
            connection,
            new InventorySearchRequest(1, 24, Makes: ["Toyota"], SellerTypes: [SellerTaxonomy.Insurance]),
            new[] { InventoryFacetsV2Groups.Makes, InventoryFacetsV2Groups.SellerTypes }
        ]));
        var sql = command.CommandText.ToLowerInvariant();

        Assert.Contains("with base as materialized", sql);
        Assert.Contains("matches_makes", sql);
        Assert.Contains("matches_sellertypes", sql);
        var makesBranch = FacetBranch(sql, InventoryFacetsV2Groups.Makes);
        var sellersBranch = FacetBranch(sql, InventoryFacetsV2Groups.SellerTypes);
        Assert.DoesNotContain("matches_makes", makesBranch);
        Assert.Contains("matches_sellertypes", makesBranch);
        Assert.DoesNotContain("matches_sellertypes", sellersBranch);
        Assert.Contains("matches_makes", sellersBranch);
        Assert.DoesNotContain("payload", sql);
        Assert.Contains(command.Parameters.Cast<NpgsqlParameter>(), parameter => parameter.ParameterName == "facet_makes");
        Assert.Contains(command.Parameters.Cast<NpgsqlParameter>(), parameter => parameter.ParameterName == "facet_seller_types");
    }

    [Fact]
    public async Task Missing_seller_is_unclassified_and_special_titles_stay_excluded()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistAsync(new AuctionVehicle
        {
            LotNumber = "missing-seller",
            Platform = "copart",
            Make = "Honda",
            Title = "CERTIFICATE OF DESTRUCTION"
        }, DateTimeOffset.Parse("2026-08-31T12:00:00Z"), CancellationToken.None);

        var result = await store.GetInventoryFacetsV2Async(
            new InventoryFacetsV2Request(
                new InventorySearchRequest(1, 24, ExcludeSpecialTitles: true),
                [InventoryFacetsV2Groups.SellerTypes, InventoryFacetsV2Groups.Titles]),
            CancellationToken.None);

        Assert.Equal(0, result.Total);
        Assert.DoesNotContain(result.Facets[InventoryFacetsV2Groups.SellerTypes], item => item.Value == SellerTaxonomy.Unclassified);
        Assert.DoesNotContain(result.Facets[InventoryFacetsV2Groups.Titles], item => item.Value == TitleFacetCategory.Special);
    }

    [Fact]
    public void Endpoint_is_parallel_token_protected_and_does_not_replace_legacy_routes()
    {
        var source = File.ReadAllText(FindRepositoryFile("Program.cs"));

        Assert.Contains("/api/v1/inventory/facets-v2", source, StringComparison.Ordinal);
        Assert.Contains("HasValidReadToken(context, inventoryReadToken)", source, StringComparison.Ordinal);
        Assert.Contains("/api/v1/inventory/search", source, StringComparison.Ordinal);
        Assert.Contains("/api/v1/inventory/summary", source, StringComparison.Ordinal);
    }

    private static int Count(InventoryFacetsV2Response response, string group, string value) =>
        response.Facets[group].SingleOrDefault(item => string.Equals(item.Value, value, StringComparison.OrdinalIgnoreCase))?.Count ?? 0;

    private static string FacetBranch(string sql, string group)
    {
        var marker = $"'{group.ToLowerInvariant()}'::text";
        var start = sql.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Missing generated branch for {group}.");
        var end = sql.IndexOf("union all", start, StringComparison.Ordinal);
        return end < 0 ? sql[start..] : sql[start..end];
    }

    private static async Task<InMemorySnapshotStore> CreateStoreAsync()
    {
        var store = new InMemorySnapshotStore();
        var observedAt = DateTimeOffset.Parse("2026-08-31T12:00:00Z");
        await store.PersistAsync(Vehicle("1", "copart", "Toyota", "Camry", 2020, "FL", SellerTaxonomy.Insurance), observedAt, CancellationToken.None);
        await store.PersistAsync(Vehicle("2", "copart", "Toyota", "Corolla", 2021, "FL", SellerTaxonomy.Dealer), observedAt.AddMinutes(1), CancellationToken.None);
        await store.PersistAsync(Vehicle("3", "iaai", "Ford", "Focus", 2019, "TX", SellerTaxonomy.Insurance), observedAt.AddMinutes(2), CancellationToken.None);
        return store;
    }

    private static AuctionVehicle Vehicle(string lot, string platform, string make, string model, int year, string state, string sellerType) => new()
    {
        LotNumber = lot,
        Platform = platform,
        Make = make,
        Model = model,
        Year = year,
        VehicleType = "Automobile",
        Title = "CLEAR",
        Location = new VehicleLocation { State = state, Display = $"{state} Yard" },
        Seller = new AuctionSeller { Type = sellerType },
        Pricing = new PricingInfo { CurrentBidUsd = 1000 + year },
        Auction = new AuctionInfo { AuctionAt = DateTimeOffset.Parse("2026-09-01T12:00:00Z") }
    };

    private static string FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = directory.EnumerateFiles(fileName, SearchOption.AllDirectories).FirstOrDefault();
            if (candidate is not null) return candidate.FullName;
            directory = directory.Parent;
        }
        throw new FileNotFoundException(fileName);
    }
}
