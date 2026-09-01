using Lsc.Inventory.Api.Eligibility;
using Lsc.Inventory.Api.Storage;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class EligibilityAuditStoreTests
{
    [Fact]
    public async Task Returns_only_discarded_items_with_pagination()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistEligibilityDecisionAsync(Evaluation("100", "DESCARTAR", "D05", "Inundado"), DateTimeOffset.Parse("2026-08-25T12:00:00Z"), CancellationToken.None);
        await store.PersistEligibilityDecisionAsync(Evaluation("200", "DESCARTAR", "D03", "Daño de bajos"), DateTimeOffset.Parse("2026-08-25T13:00:00Z"), CancellationToken.None);
        await store.PersistEligibilityDecisionAsync(Evaluation("300", "CARGAR", null, null), DateTimeOffset.Parse("2026-08-25T14:00:00Z"), CancellationToken.None);

        var firstPage = await store.GetDiscardedEligibilityDecisionsAsync(1, 1, null, null, CancellationToken.None);

        Assert.Equal(2, firstPage.Total);
        Assert.Equal(2, firstPage.TotalPages);
        Assert.Single(firstPage.Items);
        Assert.Equal("200", firstPage.Items[0].Evaluation.LotNumber);
        Assert.Equal(2, firstPage.RuleSummary.Count);
    }

    [Fact]
    public async Task Filters_by_rule_and_masked_vin()
    {
        var store = new InMemorySnapshotStore();
        await store.PersistEligibilityDecisionAsync(Evaluation("100", "DESCARTAR", "D05", "Inundado", "***1234"), DateTimeOffset.UtcNow, CancellationToken.None);
        await store.PersistEligibilityDecisionAsync(Evaluation("200", "DESCARTAR", "D03", "Daño de bajos", "***5678"), DateTimeOffset.UtcNow, CancellationToken.None);

        var result = await store.GetDiscardedEligibilityDecisionsAsync(1, 25, "d05", "1234", CancellationToken.None);

        Assert.Single(result.Items);
        Assert.Equal("100", result.Items[0].Evaluation.LotNumber);
        Assert.Equal("D05", result.Items[0].Evaluation.DiscardReasons[0].Code);
    }

    private static EligibilityEvaluation Evaluation(string lot, string decision, string? code, string? name, string vin = "***0000")
    {
        var reasons = code is null
            ? Array.Empty<EligibilityReason>()
            : [new EligibilityReason(code, name!, "Evidencia explícita.", ["damage_description"], new Dictionary<string, object?> { ["damage_description"] = name })];
        return new EligibilityEvaluation(
            decision,
            decision != "DESCARTAR",
            lot,
            "copart",
            vin,
            reasons,
            [],
            [],
            ["vin", "sale_date"],
            AuctionEligibilityEvaluator.RuleVersion);
    }
}
