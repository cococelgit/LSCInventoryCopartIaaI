using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Lsc.Inventory.Api.Normalization;
using Lsc.Inventory.Api.Options;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Classification;

public interface ISellerClassifier
{
    Task<SellerClassification> ClassifyAsync(
        string platform,
        string? sellerName,
        string? providerType,
        string? providerClass,
        string? providerTextClass,
        CancellationToken cancellationToken);
}

public sealed class OpenAiSellerClassifier : ISellerClassifier
{
    private readonly HttpClient _httpClient;
    private readonly ISellerClassificationStore _store;
    private readonly SellerClassifierOptions _options;
    private readonly ILogger<OpenAiSellerClassifier> _logger;

    public OpenAiSellerClassifier(
        HttpClient httpClient,
        ISellerClassificationStore store,
        IOptions<SellerClassifierOptions> options,
        ILogger<OpenAiSellerClassifier> logger)
    {
        _httpClient = httpClient;
        _store = store;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SellerClassification> ClassifyAsync(
        string platform,
        string? sellerName,
        string? providerType,
        string? providerClass,
        string? providerTextClass,
        CancellationToken cancellationToken)
    {
        var deterministic = SellerTaxonomy.ClassifyDetailed(providerType, providerClass, providerTextClass, sellerName);
        var normalizedName = SellerTaxonomy.NormalizeName(sellerName);
        var normalizedPlatform = string.IsNullOrWhiteSpace(platform) ? "unknown" : platform.Trim().ToLowerInvariant();

        if (deterministic.EvidenceType is "provider_field" or "deterministic_rule" || string.IsNullOrWhiteSpace(normalizedName))
        {
            if (!string.IsNullOrWhiteSpace(normalizedName))
                await _store.UpsertAsync(normalizedPlatform, normalizedName, sellerName!.Trim(), deterministic with { PromptVersion = _options.PromptVersion }, cancellationToken);
            return deterministic with { PromptVersion = _options.PromptVersion };
        }

        var cached = await _store.GetAsync(normalizedPlatform, normalizedName, cancellationToken);
        if (cached is not null && string.Equals(cached.PromptVersion, _options.PromptVersion, StringComparison.Ordinal))
            return cached;

        if (!_options.Enabled || string.IsNullOrWhiteSpace(GetApiKey()))
        {
            var disabled = deterministic with
            {
                Category = SellerTaxonomy.Unknown,
                Confidence = 0m,
                NeedsReview = true,
                Evidence = "openai_unavailable",
                Reason = "OpenAI classification is not enabled or the server-side key is unavailable.",
                EvidenceType = "insufficient_evidence",
                Model = null,
                PromptVersion = _options.PromptVersion
            };
            await _store.UpsertAsync(normalizedPlatform, normalizedName, sellerName!.Trim(), disabled, cancellationToken);
            return disabled;
        }

        var modelResult = await ClassifyWithOpenAiAsync(
            normalizedPlatform,
            sellerName!.Trim(),
            normalizedName,
            providerType,
            providerClass,
            providerTextClass,
            cancellationToken);

        await _store.UpsertAsync(normalizedPlatform, normalizedName, sellerName.Trim(), modelResult, cancellationToken);
        return modelResult;
    }

    private async Task<SellerClassification> ClassifyWithOpenAiAsync(
        string platform,
        string rawName,
        string normalizedName,
        string? providerType,
        string? providerClass,
        string? providerTextClass,
        CancellationToken cancellationToken)
    {
        var request = new
        {
            model = _options.Model,
            messages = new object[]
            {
                new { role = "system", content = SellerClassifierPrompt.System },
                new { role = "user", content = string.Format(SellerClassifierPrompt.UserTemplate, rawName, normalizedName, platform, providerType ?? string.Empty, providerClass ?? string.Empty) }
            },
            response_format = SellerClassifierPrompt.ResponseFormat,
            temperature = 0
        };

        try
        {
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
            {
                Content = JsonContent.Create(request)
            };
            requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", GetApiKey());
            using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
            response.EnsureSuccessStatusCode();
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cancellationToken), cancellationToken: cancellationToken);
            var content = document.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(content)) throw new InvalidOperationException("OpenAI returned empty seller classification content.");
            var result = JsonSerializer.Deserialize<OpenAiSellerClassification>(content, JsonOptions);
            if (result is null || !SellerTaxonomy.IsAllowedCategory(result.Category) || result.Category == SellerTaxonomy.Unclassified)
                throw new InvalidOperationException("OpenAI returned an invalid seller taxonomy category.");

            var confidence = Math.Clamp(result.Confidence, 0m, 1m);
            var accepted = confidence >= _options.MinimumAcceptedConfidence && result.Category != SellerTaxonomy.Unknown;
            return new SellerClassification(
                accepted ? result.Category : SellerTaxonomy.Unknown,
                accepted ? confidence : 0m,
                !accepted || result.NeedsReview,
                "openai_json_schema",
                result.Reason,
                accepted ? "name_only" : "insufficient_evidence",
                _options.Model,
                _options.PromptVersion);
        }
        catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidOperationException)
        {
            _logger.LogWarning(exception, "Seller classification failed for {SellerName}; storing Unknown.", rawName);
            return new SellerClassification(
                SellerTaxonomy.Unknown,
                0m,
                true,
                "openai_error",
                "Classification failed safely and was stored as Unknown.",
                "insufficient_evidence",
                _options.Model,
                _options.PromptVersion);
        }
    }

    private string GetApiKey() => !string.IsNullOrWhiteSpace(_options.ApiKey)
        ? _options.ApiKey
        : Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? string.Empty;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record OpenAiSellerClassification(
        string Category,
        decimal Confidence,
        bool NeedsReview,
        string Reason,
        string EvidenceType);
}
