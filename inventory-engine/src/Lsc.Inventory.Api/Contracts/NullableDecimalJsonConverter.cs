using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lsc.Inventory.Api.Contracts;

public sealed class NullableDecimalJsonConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.Number => reader.GetDecimal(),
            JsonTokenType.String => TryParse(reader.GetString()),
            JsonTokenType.StartObject => ReadObjectValue(ref reader),
            _ => throw new JsonException($"Expected a decimal, string, object, or null but found {reader.TokenType}.")
        };
    }

    private static decimal? ReadObjectValue(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        if (root.TryGetProperty("value", out var value))
        {
            return value.ValueKind switch
            {
                JsonValueKind.Number => value.GetDecimal(),
                JsonValueKind.String => TryParse(value.GetString()),
                _ => null
            };
        }

        return null;
    }

    private static decimal? TryParse(string? value) =>
        decimal.TryParse(value, NumberStyles.Number | NumberStyles.AllowCurrencySymbol, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value.HasValue)
        {
            writer.WriteNumberValue(value.Value);
            return;
        }

        writer.WriteNullValue();
    }
}
