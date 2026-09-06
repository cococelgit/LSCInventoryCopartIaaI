using System;
using System.IO;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class CopartCatchUpSafetyContractTests
{
    [Fact]
    public void Copart_catch_up_guarded_image_count_does_not_call_array_length_on_scalar_json()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.cs"));
        var methodStart = source.IndexOf("public async Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartCatchUpCandidatesAsync", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "Copart catch-up candidate selector must remain present.");
        var methodEnd = source.IndexOf("public async Task<IReadOnlyCollection<StoredVehicleSnapshot>> GetRecentAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "Copart catch-up selector boundary must remain discoverable.");
        var method = source[methodStart..methodEnd];

        Assert.Contains("join lateral", method);
        Assert.Contains("order by versions.observed_at desc, versions.id desc", method);
        Assert.Contains("jsonb_typeof(latest.payload->'images') = 'array'", method);
        Assert.Contains("then jsonb_array_length(latest.payload->'images')", method);
        Assert.DoesNotContain("jsonb_array_length(coalesce(latest.payload->'images'", method);
    }

    [Fact]
    public void IaaI_backfill_filters_lots_before_loading_the_latest_snapshot()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.cs"));
        var methodStart = source.IndexOf("public async Task<IReadOnlyList<StoredVehicleSnapshot>> GetIaaIConditionBackfillCandidatesAsync", StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "IAAI backfill candidate selector must remain present.");
        var methodEnd = source.IndexOf("public async Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartCatchUpCandidatesAsync", methodStart, StringComparison.Ordinal);
        Assert.True(methodEnd > methodStart, "IAAI selector boundary must remain discoverable.");
        var method = source[methodStart..methodEnd];

        Assert.Contains("join lateral", method);
        Assert.Contains("where lots.platform = 'iaai'", method);
        Assert.Contains("order by versions.observed_at desc, versions.id desc", method);
        Assert.DoesNotContain("select distinct on (versions.lot_key)", method);
    }

    [Fact]
    public void Candidate_queries_have_a_covering_latest_version_index()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.cs"));
        Assert.Contains("ix_auction_lot_versions_lot_observed_id", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Copart_catch_up_uses_tolerant_snapshot_deserialization()
    {
        var source = File.ReadAllText(FindRepositoryFile("PostgresSnapshotStore.cs"));
        var methodStart = source.IndexOf("public async Task<IReadOnlyList<StoredVehicleSnapshot>> GetCopartCatchUpCandidatesAsync", StringComparison.Ordinal);
        var methodEnd = source.IndexOf("public async Task<IReadOnlyCollection<StoredVehicleSnapshot>> GetRecentAsync", methodStart, StringComparison.Ordinal);
        var method = source[methodStart..methodEnd];

        Assert.Contains("CreateStoredVehicleJsonOptions()", method);
        Assert.Contains("DeserializeStoredVehicle(rawJson, lotKey, jsonOptions)", method);
        Assert.DoesNotContain("JsonSerializer.Deserialize<AuctionVehicle>(rawJson, jsonOptions)", method);
    }

    [Fact]
    public void Catch_up_cli_uses_application_stopping_token()
    {
        var source = File.ReadAllText(FindRepositorySourceFile("Program.cs"));
        var marker = "IAuctionsApiCopartCatchUpProcessor";
        var methodStart = source.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(methodStart >= 0, "Catch-up CLI branch must resolve the Copart processor.");
        var branchEnd = source.IndexOf("if (args.Contains(\"--iaai-auctionsapi-backfill\"", methodStart, StringComparison.Ordinal);
        Assert.True(branchEnd > methodStart, "Catch-up CLI branch boundary must remain discoverable.");
        var branch = source[methodStart..branchEnd];
        Assert.Contains("app.Lifetime.ApplicationStopping", branch);
    }

    [Fact]
    public void Automatic_copart_runtime_runs_without_a_maximum_lot_argument()
    {
        var workflow = File.ReadAllText(FindRepositoryWorkflowFile("restore-alternating-auctionsapi-schedules.yml"));
        Assert.Contains(".command=[\"/app/run-auctionsapi-dry-run.sh\"]", workflow);
        Assert.Contains(".args=[\"copart\",\"run\"]", workflow);
        Assert.DoesNotContain("[\"copart\",\"run\",\"500\"]", workflow);
        Assert.DoesNotContain("--maximum 500", workflow, StringComparison.OrdinalIgnoreCase);
    }

    private static string FindRepositoryWorkflowFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, ".github", "workflows", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate workflow {fileName} from {AppContext.BaseDirectory}");
    }

    private static string FindRepositorySourceFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Lsc.Inventory.Api", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {fileName} from {AppContext.BaseDirectory}");
    }

    private static string FindRepositoryFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "src", "Lsc.Inventory.Api", "Storage", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        throw new FileNotFoundException($"Could not locate {fileName} from {AppContext.BaseDirectory}");
    }
}
