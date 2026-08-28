using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lsc.Inventory.Api.Contracts;

/// <summary>
/// Accepts legacy string-like JSON values persisted as a scalar, number, or structured object.
/// Structured values are reduced to a meaningful source field when present and otherwise retained as compact JSON.
/// </summary>
public sealed class FlexibleStringJsonConverter : JsonConverter<string?>
{
    private static readonly string[] PreferredObjectFields = ["value", "label", "name", "description", "engine", "cylinders"];

    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => ReadRawValue(ref reader),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.StartObject or JsonTokenType.StartArray => ReadStructuredValue(ref reader),
            _ => throw new JsonException($"Unsupported JSON token {reader.TokenType} for a flexible string value.")
        };
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }

    private static string ReadRawValue(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }

    private static string ReadStructuredValue(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            foreach (var field in PreferredObjectFields)
            {
                if (document.RootElement.TryGetProperty(field, out var candidate) && candidate.ValueKind == JsonValueKind.String)
                    return candidate.GetString() ?? string.Empty;
            }
        }

        return document.RootElement.GetRawText();
    }
}
