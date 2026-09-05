using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lsc.Inventory.Api.Contracts;

/// <summary>
/// Accepts the provider variants observed for vehicle_specs.engine:
/// null, a scalar string/number, or a structured object.
/// Unknown scalar/object shapes are preserved in Raw instead of aborting the lot.
/// </summary>
public sealed class VehicleEngineJsonConverter : JsonConverter<VehicleEngine?>
{
    public override VehicleEngine? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return reader.TokenType switch
        {
            JsonTokenType.Null => null,
            JsonTokenType.String => new VehicleEngine { Raw = reader.GetString() },
            JsonTokenType.Number => new VehicleEngine { Raw = reader.GetDecimal().ToString(CultureInfo.InvariantCulture) },
            JsonTokenType.StartObject => ReadObject(ref reader),
            _ => throw new JsonException($"Expected vehicle_specs.engine to be null, string, number, or object but found {reader.TokenType}.")
        };
    }

    private static VehicleEngine ReadObject(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        return new VehicleEngine
        {
            SizeLiters = ReadText(root, "size_l") ?? ReadText(root, "size") ?? ReadText(root, "liters"),
            Horsepower = ReadDecimal(root, "hp") ?? ReadDecimal(root, "horsepower"),
            Layout = ReadText(root, "layout") ?? ReadText(root, "type") ?? ReadText(root, "configuration"),
            Raw = ReadText(root, "raw") ?? ReadText(root, "name") ?? root.GetRawText()
        };
    }

    private static string? ReadText(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText()
        };
    }

    private static decimal? ReadDecimal(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)) return number;
        if (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed)) return parsed;
        return null;
    }

    public override void Write(Utf8JsonWriter writer, VehicleEngine? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStartObject();
        writer.WriteString("size_l", value.SizeLiters);
        if (value.Horsepower is decimal horsepower) writer.WriteNumber("hp", horsepower);
        writer.WriteString("layout", value.Layout);
        writer.WriteString("raw", value.Raw);
        writer.WriteEndObject();
    }
}
