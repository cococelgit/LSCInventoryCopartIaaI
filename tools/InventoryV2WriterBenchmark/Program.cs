using System.Diagnostics;
using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

var dataDirectory = Environment.GetEnvironmentVariable("LSC_V2_DATA_DIR")
    ?? throw new InvalidOperationException("LSC_V2_DATA_DIR is required.");
var password = Environment.GetEnvironmentVariable("LSC_V2_TEST_PG_PASSWORD")
    ?? throw new InvalidOperationException("LSC_V2_TEST_PG_PASSWORD is required.");
var host = Environment.GetEnvironmentVariable("LSC_V2_TEST_PG_HOST") ?? "localhost";
var database = Environment.GetEnvironmentVariable("LSC_V2_TEST_PG_DATABASE") ?? "lsc_v2_benchmark";
var user = Environment.GetEnvironmentVariable("LSC_V2_TEST_PG_USER") ?? "postgres";
var platform = (Environment.GetEnvironmentVariable("LSC_V2_PLATFORM") ?? "copart").Trim().ToLowerInvariant();
var domainId = platform == "iaai" ? "1" : "3";

var files = Directory.GetFiles(dataDirectory, "*.json").OrderBy(static path => path, StringComparer.Ordinal).ToArray();
if (files.Length == 0) throw new InvalidOperationException($"No JSON files found in {dataDirectory}.");

var vehicles = new List<AuctionVehicle>(5000);
foreach (var file in files)
{
    using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(file));
    if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) continue;
    foreach (var row in data.EnumerateArray())
    {
        var provider = AuctionsApiCanonicalMapper.MapVehicle(row, platform);
        if (provider is null || !string.Equals(provider.DomainId, domainId, StringComparison.OrdinalIgnoreCase)) continue;
        foreach (var vehicle in AuctionsApiCanonicalMapper.ToAuctionVehicles(provider))
        {
            if (string.IsNullOrWhiteSpace(vehicle.LotNumber)) continue;
            vehicles.Add(vehicle);
            if (vehicles.Count >= 5000) break;
        }
        if (vehicles.Count >= 5000) break;
    }
    if (vehicles.Count >= 5000) break;
}
if (vehicles.Count < 5000)
    throw new InvalidOperationException($"Expected 5,000 real lots but mapped only {vehicles.Count}.");

var persistence = Options.Create(new PersistenceOptions
{
    Provider = "Postgres",
    PostgreSqlHost = host,
    Database = database,
    DatabaseUser = user,
    AccessToken = password,
    ManagedIdentityClientId = "local-benchmark",
    RuntimePrincipalName = user,
    RequireTls = false,
    CommandTimeoutSeconds = 120,
});
var v2 = Options.Create(new InventoryV2Options { ShadowWriteEnabled = true, BatchSize = 1000 });
var store = new PostgresSnapshotStore(persistence, NullLogger<PostgresSnapshotStore>.Instance, inventoryV2Options: v2);
await store.PrepareInventoryV2SchemaAsync(CancellationToken.None);

var connectionString = new NpgsqlConnectionStringBuilder
{
    Host = host,
    Database = database,
    Username = user,
    Password = password,
    SslMode = SslMode.Disable,
}.ConnectionString;
await ExecuteAsync("update inventory_v2_schema_state set writer_enabled = true, reader_enabled = false where schema_name = 'inventory-current-v2';");

var observedAt = DateTimeOffset.UtcNow;
var firstThousand = vehicles.Take(1000).Select(vehicle => new InventoryV2BatchItem(vehicle, observedAt)).ToArray();
var thousandWall = Stopwatch.StartNew();
var thousand = await store.WriteShadowBatchAsync(firstThousand, CancellationToken.None);
thousandWall.Stop();
var unchangedWall = Stopwatch.StartNew();
var thousandUnchanged = await store.WriteShadowBatchAsync(firstThousand, CancellationToken.None);
unchangedWall.Stop();

await ExecuteAsync("truncate table inventory_media_current_v2, inventory_current_v2;");
var fiveThousandWall = Stopwatch.StartNew();
var batchResults = new List<InventoryV2BatchWriteResult>();
foreach (var batch in vehicles.Take(5000).Chunk(1000))
    batchResults.Add(await store.WriteShadowBatchAsync(batch.Select(vehicle => new InventoryV2BatchItem(vehicle, observedAt)).ToArray(), CancellationToken.None));
fiveThousandWall.Stop();

await using var verifyConnection = new NpgsqlConnection(connectionString);
await verifyConnection.OpenAsync();
await using var verify = verifyConnection.CreateCommand();
verify.CommandText = "select count(*)::integer, (select count(*)::integer from inventory_media_current_v2) from inventory_current_v2;";
await using var reader = await verify.ExecuteReaderAsync();
await reader.ReadAsync();

var summary = new
{
    Dataset = new
    {
        Platform = platform,
        Files = files.Length,
        MappedLots = vehicles.Count,
    },
    Thousand = new
    {
        Result = thousand,
        WallMs = thousandWall.ElapsedMilliseconds,
        MeetsTarget = thousandWall.Elapsed <= TimeSpan.FromSeconds(10),
    },
    ThousandUnchanged = new
    {
        Result = thousandUnchanged,
        WallMs = unchangedWall.ElapsedMilliseconds,
    },
    FiveThousand = new
    {
        Batches = batchResults.Count,
        Created = batchResults.Sum(static result => result.Created),
        Updated = batchResults.Sum(static result => result.Updated),
        Unchanged = batchResults.Sum(static result => result.Unchanged),
        Stale = batchResults.Sum(static result => result.Stale),
        MediaRowsWritten = batchResults.Sum(static result => result.MediaRowsWritten),
        WriterDurationMs = batchResults.Sum(static result => result.DurationMs),
        WallMs = fiveThousandWall.ElapsedMilliseconds,
        MeetsTarget = fiveThousandWall.Elapsed <= TimeSpan.FromSeconds(45),
    },
    PersistedCurrentRows = reader.GetInt32(0),
    PersistedMediaRows = reader.GetInt32(1),
};

if (summary.Thousand.Result.DistinctRows != 1000 || summary.ThousandUnchanged.Result.Unchanged != 1000)
    throw new InvalidOperationException($"1,000-row benchmark invariants failed: {JsonSerializer.Serialize(summary)}");
if (summary.FiveThousand.Created != 5000 || summary.PersistedCurrentRows != 5000)
    throw new InvalidOperationException($"5,000-row benchmark invariants failed: {JsonSerializer.Serialize(summary)}");
if (!summary.Thousand.MeetsTarget || !summary.FiveThousand.MeetsTarget)
    throw new InvalidOperationException($"Benchmark targets failed: {JsonSerializer.Serialize(summary)}");

Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

async Task ExecuteAsync(string sql)
{
    await using var connection = new NpgsqlConnection(connectionString);
    await connection.OpenAsync();
    await using var command = connection.CreateCommand();
    command.CommandTimeout = 120;
    command.CommandText = sql;
    await command.ExecuteNonQueryAsync();
}
