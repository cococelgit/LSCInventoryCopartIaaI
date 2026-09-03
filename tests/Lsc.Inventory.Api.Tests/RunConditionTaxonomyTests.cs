using Lsc.Inventory.Api.Normalization;
using Xunit;

namespace Lsc.Inventory.Api.Tests;

public sealed class RunConditionTaxonomyTests
{
    [Theory]
    [InlineData("RUNS AND DRIVES", "RUNS_AND_DRIVES")]
    [InlineData("Runs & Drives", "RUNS_AND_DRIVES")]
    [InlineData("RUNS/DRIVES", "RUNS_AND_DRIVES")]
    [InlineData("RUN AND DRIVE", "RUNS_AND_DRIVES")]
    [InlineData("STARTS", "STARTS")]
    [InlineData("STATIONARY", "STATIONARY")]
    [InlineData("No Information", "UNVERIFIED")]
    [InlineData("", "UNVERIFIED")]
    public void Normalizes_provider_variants_to_the_canonical_value(string raw, string expected)
    {
        Assert.Equal(expected, RunConditionTaxonomy.Normalize(raw));
    }

    [Fact]
    public void Does_not_promote_unknown_condition_text_to_run_and_drive()
    {
        Assert.Equal(RunConditionTaxonomy.Unverified, RunConditionTaxonomy.Normalize("Engine status unavailable"));
    }
}
