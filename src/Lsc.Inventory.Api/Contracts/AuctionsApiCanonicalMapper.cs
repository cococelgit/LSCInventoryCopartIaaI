using System.Globalization;
using System.Text.Json;

namespace Lsc.Inventory.Api.Contracts;

public static class AuctionsApiCanonicalMapper
{
    public static AuctionsApiProviderVehicle? MapVehicle(JsonElement row, string platform)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        var lots = new List<AuctionsApiProviderLot>();
        if (TryGetArray(row, "lots", out var nestedLots))
        {
            foreach (var lot in nestedLots.EnumerateArray())
            {
                var mappedLot = MapLot(lot, row, platform);
                if (mappedLot is not null) lots.Add(mappedLot);
            }
        }
        else
        {
            var mappedLot = MapLot(row, row, platform);
            if (mappedLot is not null) lots.Add(mappedLot);
        }

        var domain = FirstValue(row, "domain.id", "domain_id");
        if (lots.Count > 0 && string.IsNullOrWhiteSpace(domain)) domain = lots[0].DomainId;
        return new AuctionsApiProviderVehicle(
            platform.Trim().ToLowerInvariant(),
            domain,
            FirstValue(row, "id", "car_id"),
            IntValue(row, "year"),
            EnumValue(row, "manufacturer"),
            EnumValue(row, "model"),
            EnumValue(row, "generation"),
            EnumValue(row, "vehicle_type"),
            EnumValue(row, "body_type"),
            EnumValue(row, "fuel"),
            EnumValue(row, "engine"),
            IntValue(row, "cylinders"),
            EnumValue(row, "transmission"),
            EnumValue(row, "drive_wheel"),
            lots,
            row.Clone());
    }

    public static AuctionsApiArchivedOutcome? MapArchived(JsonElement row, string platform)
    {
        if (row.ValueKind != JsonValueKind.Object) return null;
        return new AuctionsApiArchivedOutcome(
            platform.Trim().ToLowerInvariant(),
            FirstValue(row, "domain.id", "domain_id"),
            FirstValue(row, "lot", "lot_number"),
            FirstValue(row, "lot_id", "external_id"),
            EnumValue(row, "status"),
            MoneyValue(row, "bid"),
            MoneyValue(row, "final_bid"),
            MoneyValue(row, "buy_now"),
            DateValue(row, "sale_date"),
            DateTimeValue(row, "archived_at"),
            row.Clone());
    }

    private static AuctionsApiProviderLot? MapLot(JsonElement lot, JsonElement vehicle, string platform)
    {
        if (lot.ValueKind != JsonValueKind.Object) return null;
        var lotNumber = FirstValue(lot, "lot", "lot_number", "external_id", "id");
        var domain = FirstValue(lot, "domain.id", "domain_id") ?? FirstValue(vehicle, "domain.id", "domain_id");
        var flags = new Dictionary<string, bool?>(StringComparer.OrdinalIgnoreCase)
        {
            ["insurance"] = BoolValue(lot, "seller.is_insurance", "is_insurance"),
            ["rental"] = BoolValue(lot, "seller.is_rental", "is_rental"),
            ["credit_company"] = BoolValue(lot, "seller.is_credit_company", "is_credit_company"),
        };
        return new AuctionsApiProviderLot(
            platform.Trim().ToLowerInvariant(),
            domain,
            lotNumber,
            FirstValue(lot, "external_id", "id"),
            FirstValue(lot, "vin") ?? FirstValue(vehicle, "vin"),
            EnumValue(lot, "status"),
            EnumValue(lot, "seller_type"),
            FirstValue(lot, "seller.name", "seller"),
            LocationValue(lot, "location"),
            flags,
            EnumValue(lot, "title"),
            EnumValue(lot, "detailed_title"),
            EnumValue(lot, "condition"),
            EnumValue(lot, "damage.main"),
            EnumValue(lot, "damage.second"),
            EnumValue(lot, "odometer.status"),
            EnumValue(lot, "airbags"),
            MoneyValue(lot, "bid"),
            MoneyValue(lot, "final_bid"),
            MoneyValue(lot, "buy_now"),
            MoneyValue(lot, "sale_price"),
            DateValue(lot, "sale_date"),
            DateTimeValue(lot, "archived_at"),
            DateTimeValue(lot, "updated_at") ?? DateTimeValue(vehicle, "updated_at"),
            lot.Clone(),
            DecimalValue(lot, "odometer.miles", "odometer.mi", "odometer"),
            DecimalValue(lot, "odometer.km", "odometer.kilometers"),
            BoolValue(lot, "keys_available", "key_available", "keys"));
    }

    private static AuctionLocation? LocationValue(JsonElement value, string path)
    {
        var raw = At(value, path);
        if (raw is null || raw.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        var item = raw.Value;
        return new AuctionLocation
        {
            Platform = FirstValue(item, "platform.name", "platform"),
            FacilityId = FirstValue(item, "facility_id", "branch_id", "id"),
            Name = FirstValue(item, "name", "branch", "location", "office"),
            City = FirstValue(item, "city", "city.name"),
            State = FirstValue(item, "state", "state.code", "state.name"),
        };
    }

    private static AuctionsApiEnumValue? EnumValue(JsonElement value, string path)
    {
        var raw = At(value, path);
        if (raw is null || raw.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var item = raw.Value;
        string? id = null;
        string? name = null;
        if (item.ValueKind == JsonValueKind.Object)
        {
            id = FirstValue(item, "id", "code", "value");
            name = FirstValue(item, "name", "label", "text");
        }
        else
        {
            name = Scalar(item);
        }
        if (id is null && name is null) return null;
        var normalized = Normalize(name ?? id);
        return new AuctionsApiEnumValue(id, name, normalized, item.Clone());
    }

    private static AuctionsApiMoneyValue? MoneyValue(JsonElement value, string path)
    {
        var raw = At(value, path);
        if (raw is null || raw.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var item = raw.Value;
        var number = DecimalValue(item.ValueKind == JsonValueKind.Object ? At(item, "value") ?? item : item);
        var updatedAt = item.ValueKind == JsonValueKind.Object ? DateTimeValue(item, "updated_at") : null;
        return new AuctionsApiMoneyValue(number, updatedAt, item.Clone());
    }

    private static AuctionsApiDateValue? DateValue(JsonElement value, string path)
    {
        var raw = At(value, path);
        if (raw is null || raw.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        var item = raw.Value;
        var date = item.ValueKind == JsonValueKind.Object ? DateTimeValue(item, "value") : DateTimeValue(item);
        var updatedAt = item.ValueKind == JsonValueKind.Object ? DateTimeValue(item, "updated_at") : null;
        return new AuctionsApiDateValue(date, updatedAt, item.Clone());
    }

    private static string? FirstValue(JsonElement value, params string[] paths)
    {
        foreach (var path in paths)
        {
            var item = At(value, path);
            if (item is null) continue;
            var scalar = Scalar(item.Value);
            if (!string.IsNullOrWhiteSpace(scalar)) return scalar;
        }
        return null;
    }

    private static string? Scalar(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.ToString(),
            JsonValueKind.Object => FirstValue(value, "name", "label", "value", "id", "code"),
            _ => null,
        };
    }

    private static int? IntValue(JsonElement value, string path)
    {
        var raw = FirstValue(value, path);
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static decimal? DecimalValue(JsonElement value)
    {
        var raw = value.ValueKind == JsonValueKind.Object
            ? FirstValue(value, "value", "miles", "mi", "kilometers", "km")
            : Scalar(value);
        return decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var result) ? result : null;
    }

    private static decimal? DecimalValue(JsonElement value, params string[] paths)
    {
        foreach (var path in paths)
        {
            var item = At(value, path);
            if (item is null) continue;
            var number = DecimalValue(item.Value);
            if (number.HasValue) return number;
        }
        return null;
    }

    private static bool? BoolValue(JsonElement value, params string[] paths)
    {
        var raw = FirstValue(value, paths)?.Trim();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (bool.TryParse(raw, out var result)) return result;
        return raw.ToUpperInvariant() switch
        {
            "YES" or "Y" or "AVAILABLE" or "PRESENT" or "INTACT" => true,
            "NO" or "N" or "NONE" or "MISSING" or "NOT AVAILABLE" => false,
            _ => null,
        };
    }

    private static DateTimeOffset? DateTimeValue(JsonElement value, string? path = null)
    {
        var item = path is null ? value : At(value, path);
        if (item is null) return null;
        var raw = Scalar(item.Value);
        return DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null;
    }

    private static JsonElement? At(JsonElement value, string path)
    {
        foreach (var segment in path.Split('.'))
        {
            if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(segment, out value)) return null;
        }
        return value;
    }

    private static bool TryGetArray(JsonElement value, string property, out JsonElement array)
    {
        if (value.TryGetProperty(property, out array) && array.ValueKind == JsonValueKind.Array) return true;
        array = default;
        return false;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    public static IReadOnlyList<AuctionVehicle> ToAuctionVehicles(AuctionsApiProviderVehicle provider)
    {
        return provider.Lots.Select(lot => new AuctionVehicle
        {
            Platform = provider.Platform,
            SourceProvider = AuctionsApiCanonicalContract.Source,
            LotNumber = lot.LotNumber,
            Vin = lot.Vin,
            Year = provider.Year,
            Make = provider.Manufacturer?.Name,
            Model = provider.Model?.Name,
            VehicleType = provider.VehicleType?.Name,
            VehicleSpecs = new VehicleSpecs
            {
                BodyStyle = provider.BodyType?.Name,
                FuelType = provider.Fuel?.Name,
                Transmission = provider.Transmission?.Name,
                DriveType = provider.DriveWheel?.Name,
                Airbags = lot.Airbags?.Name,
                Engine = provider.Engine is null ? null : new VehicleEngine { Raw = provider.Engine.Name },
            },
            Condition = new VehicleCondition
            {
                PrimaryDamage = lot.DamageMain?.Name,
                SecondaryDamage = lot.DamageSecond?.Name,
                HasKey = lot.HasKey,
                RunCondition = lot.Condition is null
                    ? null
                    : new RunConditionInfo
                    {
                        Value = lot.Condition.NormalizedValue,
                        Label = lot.Condition.Name,
                    },
            },
            Seller = new AuctionSeller
            {
                Name = lot.SellerName,
                RawType = lot.SellerType?.Name,
                Type = lot.SellerType?.NormalizedValue,
            },
            Location = lot.Location is null
                ? null
                : new VehicleLocation
                {
                    Display = LocationDisplay(lot.Location),
                    City = lot.Location.City,
                    State = lot.Location.State,
                    FacilityId = lot.Location.FacilityId,
                },
            OdometerInfo = lot.OdometerStatus is null
                && !lot.OdometerMiles.HasValue
                && !lot.OdometerKilometers.HasValue
                ? null
                : new OdometerInfo
                {
                    Miles = lot.OdometerMiles,
                    Kilometers = lot.OdometerKilometers,
                    Status = lot.OdometerStatus?.Name,
                },
            Auction = new AuctionInfo
            {
                LotStatus = lot.Status?.Name,
                AuctionAt = lot.SaleDate?.Value,
                IsBuyNow = lot.BuyNow?.Value is not null,
            },
            Pricing = new PricingInfo
            {
                CurrentBidUsd = lot.Bid?.Value,
                BuyNowUsd = lot.BuyNow?.Value,
                SalePriceUsd = lot.SalePrice?.Value ?? lot.FinalBid?.Value,
            },
            Media = MapMedia(lot.Raw, provider.Raw),
            RawSource = provider.Raw,
        }).ToArray();
    }

    private static string? LocationDisplay(AuctionLocation location)
    {
        var city = string.IsNullOrWhiteSpace(location.City) ? null : location.City.Trim();
        var state = string.IsNullOrWhiteSpace(location.State) ? null : location.State.Trim();
        if (city is not null && state is not null) return $"{city}, {state}";
        if (city is not null) return city;
        if (state is not null) return state;
        return string.IsNullOrWhiteSpace(location.Name) ? null : location.Name.Trim();
    }

    private static MediaInfo? MapMedia(params JsonElement[] rows)
    {
        var urls = new List<string>();
        foreach (var row in rows)
        {
            var images = At(row, "images");
            if (images is null) continue;
            CollectImageUrls(images.Value, urls);
        }

        var photos = urls
            .Where(static value => Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        return photos.Length == 0 ? null : new MediaInfo
        {
            ThumbnailsCount = photos.Length,
            Photos = photos,
        };
    }

    private static void CollectImageUrls(JsonElement value, ICollection<string> urls)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                var candidate = value.GetString();
                if (!string.IsNullOrWhiteSpace(candidate)) urls.Add(candidate);
                return;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray()) CollectImageUrls(item, urls);
                return;
            case JsonValueKind.Object:
                foreach (var property in new[] { "big", "normal", "small", "exterior", "interior", "url", "src", "large", "thumb" })
                {
                    if (value.TryGetProperty(property, out var nested)) CollectImageUrls(nested, urls);
                }
                return;
        }
    }
}
