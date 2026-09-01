using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lsc.Inventory.Api.Contracts;

/// <summary>
/// Reads nullable booleans from heterogeneous auction payloads without turning
/// an otherwise usable vehicle snapshot into a failed search response.
/// Unknown values remain null so the API never invents a condition.
/// </summary>
public sealed class NullableBooleanJsonConverter : JsonConverter<bool?>
{
    public override bool? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.True => true,
            JsonTokenType.False => false,
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.String => ReadString(reader.GetString()),
            JsonTokenType.StartObject or JsonTokenType.StartArray => ReadUnknownValue(ref reader),
            _ => null
        };
    }

    private static bool? ReadUnknownValue(ref Utf8JsonReader reader)
    {
        reader.Skip();
        return null;
    }

    private static bool? ReadNumber(ref Utf8JsonReader reader)
    {
        if (!reader.TryGetInt32(out var value)) return null;
        return value switch
        {
            1 => true,
            0 => false,
            _ => null
        };
    }

    private static bool? ReadString(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim();
        if (bool.TryParse(normalized, out var parsed)) return parsed;
        if (int.TryParse(normalized, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            return numeric switch { 1 => true, 0 => false, _ => null };

        return normalized.ToUpperInvariant() switch
        {
            "Y" or "YES" or "SI" or "SÍ" or "T" => true,
            "N" or "NO" or "F" => false,
            _ => null
        };
    }

    public override void Write(Utf8JsonWriter writer, bool? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteBooleanValue(value.Value);
        else writer.WriteNullValue();
    }
}
