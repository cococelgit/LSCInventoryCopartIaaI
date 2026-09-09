using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lsc.Inventory.Api.Options;
using Lsc.Inventory.Api.SaleAttempts;
using Lsc.Inventory.Api.Services;
using Lsc.Inventory.Api.Storage;
using Microsoft.Extensions.Logging.Abstractions;

var apiKey = Environment.GetEnvironmentVariable("AUCTIONS_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
    throw new InvalidOperationException("AUCTIONS_API_KEY is required for this read-only dry-run.");

var perPlatform = ReadIntArgument(args, "--per-platform", 20, 1, 100);
var minutes = ReadIntArgument(args, "--minutes", 4320, 1, 4320);
var outputPath = ReadStringArgument(args, "--output");
var observedAt = DateTimeOffset.UtcNow;
using var httpClient = new HttpClient { BaseAddress = new Uri("https://auctionsapi.com/api/"), Timeout = TimeSpan.FromSeconds(120) };
var options = Microsoft.Extensions.Options.Options.Create(new AuctionsApiOptions
{
    Enabled = true,
    AllowWrites = false,
    ApiKey = apiKey,
    BaseUrl = "https://auctionsapi.com/api/",
    PageSize = perPlatform,
    RequestIntervalMilliseconds = 500,
    RequestTimeoutSeconds = 120
});
var client = new AuctionsApiClient(httpClient, options, new ProviderRequestLimiter(), NullLogger<AuctionsApiClient>.Instance);
var calculator = new SellerMotivationSignalCalculator();
var store = new InMemorySaleAttemptHistoryStore();
var platformResults = new List<object>();
var allSignals = new List<SellerMotivationSignal>();

foreach (var (platform, domainId) in new[] { ("iaai", 1), ("copart", 3) })
{
    var page = await client.GetChangedLotsAsync(
        new AuctionsApiWindowRequest(domainId, minutes, 1, perPlatform, IncludePricesHistory: true),
        CancellationToken.None);
    var parsed = AuctionsApiSaleAttemptParser.Parse(page.Data, platform, observedAt);
    var snapshots = parsed.Snapshots.Take(perPlatform).ToArray();
    var signals = snapshots
        .Select(snapshot => calculator.Calculate(snapshot, "motivated_seller_v1", observedAt))
        .ToArray();
    allSignals.AddRange(signals);

    var firstPass = new List<SaleAttemptPersistenceResult>();
    var secondPass = new List<SaleAttemptPersistenceResult>();
    for (var index = 0; index < snapshots.Length; index++)
        firstPass.Add(await store.PersistAsync(snapshots[index], signals[index], CancellationToken.None));
    for (var index = 0; index < snapshots.Length; index++)
        secondPass.Add(await store.PersistAsync(snapshots[index], signals[index], CancellationToken.None));

    platformResults.Add(new
    {
        platform,
        requested = perPlatform,
        parsed.Diagnostics.LotsExamined,
        lotsAccepted = snapshots.Length,
        sourceLotsAccepted = parsed.Diagnostics.LotsAccepted,
        parsed.Diagnostics.AttemptsExamined,
        parsed.Diagnostics.AttemptsAccepted,
        parsed.Diagnostics.MissingSaleDateSkipped,
        parsed.Diagnostics.DuplicateAttemptSkipped,
        withHistory = signals.Count(item => item.HistoryAvailability == SaleAttemptHistoryAvailability.Available),
        high = signals.Count(item => item.SignalLevel == SaleAttemptSignalLevels.High),
        medium = signals.Count(item => item.SignalLevel == SaleAttemptSignalLevels.Medium),
        followUp = signals.Count(item => item.SignalLevel == SaleAttemptSignalLevels.FollowUp),
        none = signals.Count(item => item.SignalLevel == SaleAttemptSignalLevels.None),
        firstPassInserted = firstPass.Sum(item => item.InsertedAttempts),
        firstPassUpdated = firstPass.Sum(item => item.UpdatedAttempts),
        secondPassInserted = secondPass.Sum(item => item.InsertedAttempts),
        secondPassUpdated = secondPass.Sum(item => item.UpdatedAttempts),
        secondPassUnchanged = secondPass.Sum(item => item.UnchangedAttempts),
        examples = signals
            .Where(item => item.SignalLevel != SaleAttemptSignalLevels.None)
            .OrderByDescending(item => item.NotSoldCount)
            .Take(5)
            .Select(item => new
            {
                item.LotNumber,
                item.SignalLevel,
                item.NotSoldCount,
                item.HistoricalMaxBidUsd,
                item.CurrentAskUsd,
                item.AskGapPercent,
                item.ConfidencePercent,
                item.ReasonCodes
            })
            .ToArray()
    });
}

var fingerprintInput = string.Join('|', allSignals
    .OrderBy(item => item.LotKey, StringComparer.Ordinal)
    .Select(item => $"{item.LotKey}:{item.InputHash}"));
var report = new
{
    generatedAt = observedAt,
    mode = "read-only-auctionsapi-plus-in-memory-idempotency",
    pricesHistoryRequested = true,
    postgresWrites = false,
    jobsModified = false,
    perPlatform,
    minutes,
    signalFingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(fingerprintInput))).ToLowerInvariant(),
    platforms = platformResults
};
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
if (!string.IsNullOrWhiteSpace(outputPath))
{
    var fullPath = Path.GetFullPath(outputPath);
    Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
    await File.WriteAllTextAsync(fullPath, json);
}
Console.WriteLine(json);

static int ReadIntArgument(string[] arguments, string name, int fallback, int minimum, int maximum)
{
    var index = Array.FindIndex(arguments, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
    if (index < 0) return fallback;
    if (index + 1 >= arguments.Length || !int.TryParse(arguments[index + 1], out var value))
        throw new ArgumentException($"{name} requires an integer value.");
    return Math.Clamp(value, minimum, maximum);
}

static string? ReadStringArgument(string[] arguments, string name)
{
    var index = Array.FindIndex(arguments, item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
    if (index < 0) return null;
    if (index + 1 >= arguments.Length) throw new ArgumentException($"{name} requires a value.");
    return arguments[index + 1];
}
