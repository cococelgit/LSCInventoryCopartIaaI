using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lsc.Inventory.Api.Contracts;

public sealed class StringOrNumberJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.GetDecimal().ToString(CultureInfo.InvariantCulture),
            _ => throw new JsonException($"Expected a string or number but found {reader.TokenType}.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
