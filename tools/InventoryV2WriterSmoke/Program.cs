using System.Text.Json;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

var host = Environment.GetEnvironmentVariable("LSC_V2_TEST_PG_HOST") ?? "localhost";
var database = Environment.GetEnvironmentVariable("LSC_V2_TEST_PG_DATABASE") ?? "lsc_v2_writer_test";
var user = Environment.GetEnvironmentVariable("LSC_V2_TEST_PG_USER") ?? "postgres";
var password = Environment.GetEnvironmentVariable("LSC_V2_TEST_PG_PASSWORD")
    ?? throw new InvalidOperationException("LSC_V2_TEST_PG_PASSWORD is required.");

var persistence = Options.Create(new PersistenceOptions
{
    Provider = "Postgres",
    PostgreSqlHost = host,
    Database = database,
    DatabaseUser = user,
    AccessToken = password,
    ManagedIdentityClientId = "local-test",
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
await using (var connection = new NpgsqlConnection(connectionString))
{
    await connection.OpenAsync();
    await using var enable = connection.CreateCommand();
    enable.CommandText = "update inventory_v2_schema_state set writer_enabled = true, reader_enabled = false where schema_name = 'inventory-current-v2';";
    await enable.ExecuteNonQueryAsync();
}

var observedAt = DateTimeOffset.Parse("2026-09-11T12:00:00Z");
var initial = Enumerable.Range(1, 3).Select(index =>
    new InventoryV2BatchItem(Vehicle(index, 10_000m + index), observedAt)).ToArray();
var first = await store.WriteShadowBatchAsync(initial, CancellationToken.None);
var second = await store.WriteShadowBatchAsync(initial, CancellationToken.None);
var changed = await store.WriteShadowBatchAsync(
    [new InventoryV2BatchItem(Vehicle(1, 12_500m, "2026-09-11T12:00:00Z"), observedAt.AddMinutes(1))],
    CancellationToken.None);
var stale = await store.WriteShadowBatchAsync(
    [new InventoryV2BatchItem(Vehicle(1, 9_000m, "2026-09-11T11:58:00Z"), observedAt.AddMinutes(2))],
    CancellationToken.None);

await using var verifyConnection = new NpgsqlConnection(connectionString);
await verifyConnection.OpenAsync();
await using var verify = verifyConnection.CreateCommand();
verify.CommandText = """
    select
        count(*)::integer,
        count(*) filter (where record_version = 2)::integer,
        count(*) filter (where current_bid_usd = 12500)::integer,
        (select count(*)::integer from inventory_media_current_v2),
        count(*) filter (where last_seen_at = timestamptz '2026-09-11T12:02:00Z')::integer
    from inventory_current_v2;
    """;
await using var reader = await verify.ExecuteReaderAsync();
await reader.ReadAsync();
var summary = new
{
    First = first,
    Second = second,
    Changed = changed,
    Stale = stale,
    CurrentRows = reader.GetInt32(0),
    VersionTwoRows = reader.GetInt32(1),
    Bid12500Rows = reader.GetInt32(2),
    MediaRows = reader.GetInt32(3),
    StalePresenceRows = reader.GetInt32(4),
};

if (first.Created != 3 || second.Unchanged != 3 || changed.Updated != 1 || stale.Stale != 1)
    throw new InvalidOperationException($"Unexpected action counts: {JsonSerializer.Serialize(summary)}");
if (summary.CurrentRows != 3 || summary.VersionTwoRows != 1 || summary.Bid12500Rows != 1 || summary.MediaRows != 6 || summary.StalePresenceRows != 1)
    throw new InvalidOperationException($"Unexpected persisted state: {JsonSerializer.Serialize(summary)}");

Console.WriteLine(JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true }));

static AuctionVehicle Vehicle(int index, decimal bid, string sourceUpdatedAt = "2026-09-11T11:59:00Z") => new()
{
    Platform = "copart",
    SourceProvider = "auctionsapi",
    LotNumber = $"6420640{index}",
    Vin = $"1HGCM82633A00435{index}",
    Year = 2022,
    Make = "Honda",
    Model = "Accord",
    VehicleType = "Automobile",
    VehicleSpecs = new VehicleSpecs
    {
        BodyStyle = "Sedan",
        FuelType = "Gasoline",
        Transmission = "Automatic",
        DriveType = "FWD",
        Engine = new VehicleEngine { SizeLiters = "2.0", Horsepower = 192, Raw = "2.0L 4" },
    },
    Condition = new VehicleCondition
    {
        PrimaryDamage = "Front End",
        HasKey = true,
        RunCondition = new RunConditionInfo { Value = "RUNS AND DRIVES", Label = "Runs and Drives" },
    },
    OdometerInfo = new OdometerInfo { Miles = 48_000 + index, Status = "ACTUAL" },
    SaleDocument = new SaleDocument { Type = "Clean", Name = "Certificate of Title" },
    Seller = new AuctionSeller { Name = "Insurance Company", Type = "insurance", Class = "insurance" },
    Auction = new AuctionInfo { State = "FL", AuctionAt = DateTimeOffset.Parse("2026-09-12T14:00:00Z"), LotStatus = "Live", IsBuyNow = true },
    Pricing = new PricingInfo { CurrentBidUsd = bid, BuyNowUsd = 25_000m },
    Location = new VehicleLocation { Display = "MIAMI SOUTH", State = "FL", FacilityId = "36" },
    Media = new MediaInfo { Photos = [$"https://images.example.test/{index}-a.jpg", $"https://images.example.test/{index}-b.jpg"] },
    RawSource = JsonDocument.Parse($$"""
        {
          "id": {{900 + index}},
          "domain": { "id": 1 },
          "manufacturer": { "id": 7, "name": "Honda" },
          "model": { "id": 44, "name": "Accord" },
          "updated_at": "{{sourceUpdatedAt}}",
          "lots": [
            {
              "lot": "6420640{{index}}",
              "external_id": "lot-6420640{{index}}",
              "status": { "id": 2, "name": "Live" },
              "bid": { "value": {{bid}}, "updated_at": "2026-09-11T11:58:00Z" },
              "buy_now": { "value": 25000, "updated_at": "2026-09-11T11:57:00Z" },
              "images": ["https://images.example.test/{{index}}-a.jpg", "https://images.example.test/{{index}}-b.jpg"]
            }
          ]
        }
        """).RootElement.Clone(),
};
