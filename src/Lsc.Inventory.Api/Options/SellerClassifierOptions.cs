using System.ComponentModel.DataAnnotations;

namespace Lsc.Inventory.Api.Options;

public sealed class SellerClassifierOptions
{
    public const string SectionName = "SellerClassifier";

    public bool Enabled { get; init; }

    public string ApiBase { get; init; } = "https://api.openai.com/v1";

    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = "gpt-5-mini";

    public string PromptVersion { get; init; } = "seller_classifier_v1";

    [Range(0, 1)]
    public decimal MinimumAcceptedConfidence { get; init; } = 0.70m;

    [Range(1, 120)]
    public int RequestTimeoutSeconds { get; init; } = 30;
}
