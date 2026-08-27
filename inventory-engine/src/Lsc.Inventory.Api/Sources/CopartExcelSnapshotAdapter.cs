using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CsvHelper;
using CsvHelper.Configuration;
using Lsc.Inventory.Api.Contracts;
using Lsc.Inventory.Api.Options;
using Microsoft.Extensions.Options;

namespace Lsc.Inventory.Api.Sources;

public sealed record CopartSnapshotValidation(
    bool IsComplete,
    long FileSizeBytes,
    int RowCount,
    IReadOnlyList<string> Failures);

public sealed class CopartSnapshotValidationException(CopartSnapshotValidation validation)
    : IOException(string.Join(" | ", validation.Failures))
{
    public CopartSnapshotValidation Validation { get; } = validation;
}

public sealed class CopartExcelSnapshotAdapter(IOptions<CopartExcelOptions> options) : ICopartExcelSnapshotAdapter
{
    private static readonly string[] RequiredHeaders =
    [
        "Lot number", "VIN", "Year", "Make", "Model Group", "Model Detail", "Vehicle Type",
        "Sale Date M/D/CY", "Sale time (HHMM)", "Time Zone", "Damage Description", "Secondary Damage",
        "Sale Title Type", "Special Note", "Announcements", "Location state", "Location city", "Location ZIP",
        "Yard number", "Yard name", "Seller Name", "Has Keys-Yes or No", "Runs/Drives", "Odometer",
        "Odometer Brand", "Sale Status", "High Bid =non-vix,Sealed=Vix", "Buy-It-Now Price", "Image Thumbnail"
    ];

    private readonly CopartExcelOptions _options = options.Value;

    public async Task<CopartSnapshotValidation> ValidateAsync(CopartSnapshotEnvelope snapshot, CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        var content = snapshot.Content;
        if (string.IsNullOrWhiteSpace(snapshot.FileName) || !snapshot.FileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase))
            failures.Add("F01: Copart snapshot must be a .csv file.");
        if (content is null || !content.CanSeek || !content.CanRead)
            failures.Add("F01: Copart snapshot stream must be readable and seekable for validation and streaming replay.");

        if (failures.Count > 0)
            return new CopartSnapshotValidation(false, 0, 0, failures);

        var fileSize = content!.Length;
        if (fileSize < _options.MinimumFileSizeKilobytes * 1024L)
            failures.Add($"F01: Snapshot is below the {_options.MinimumFileSizeKilobytes} KB minimum.");
        if (fileSize > _options.MaximumFileSizeMegabytes * 1024L * 1024L)
            failures.Add($"F01: Snapshot exceeds the {_options.MaximumFileSizeMegabytes} MB maximum.");

        var suppliedHash = NormalizeHash(snapshot.Sha256);
        if (suppliedHash is null)
            failures.Add("F01: Snapshot SHA-256 is missing or malformed.");
        else
        {
            var computedHash = await ComputeHashAsync(content!, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(suppliedHash), Encoding.ASCII.GetBytes(computedHash)))
                failures.Add("F01: Snapshot SHA-256 does not match the provided envelope hash.");
        }

        int rows = 0;
        try
        {
            content!.Position = 0;
            using var reader = CreateReader(content);
            using var csv = CreateCsv(reader);
            if (!await csv.ReadAsync())
            {
                failures.Add("F01: Snapshot has no header row.");
            }
            else
            {
                csv.ReadHeader();
                var headers = csv.HeaderRecord ?? [];
                var missingHeaders = RequiredHeaders.Where(header => !headers.Contains(header, StringComparer.OrdinalIgnoreCase)).ToArray();
                if (missingHeaders.Length > 0)
                    failures.Add($"F03: Missing required Copart columns: {string.Join(", ", missingHeaders)}.");

                while (await csv.ReadAsync())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    rows++;
                }
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            failures.Add($"F04: CSV structure cannot be streamed safely: {exception.Message}");
        }
        finally
        {
            content!.Position = 0;
        }

        if (rows < _options.MinimumRowsForCompleteSnapshot)
            failures.Add($"F05: Snapshot has {rows} rows, below the {_options.MinimumRowsForCompleteSnapshot} completeness floor.");

        return new CopartSnapshotValidation(failures.Count == 0, fileSize, rows, failures);
    }

    public async IAsyncEnumerable<AuctionVehicle> ReadAcceptedSnapshotAsync(
        CopartSnapshotEnvelope snapshot,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var validation = await ValidateAsync(snapshot, cancellationToken);
        if (!validation.IsComplete)
            throw new CopartSnapshotValidationException(validation);

        var content = snapshot.Content ?? throw new IOException("Copart snapshot stream is unavailable.");
        content.Position = 0;
        using var reader = CreateReader(content);
        using var csv = CreateCsv(reader);
        if (!await csv.ReadAsync())
            yield break;
        csv.ReadHeader();
        var headers = csv.HeaderRecord ?? [];

        while (await csv.ReadAsync())
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuctionVehicle vehicle;
            try
            {
                var row = headers.ToDictionary(header => header, header => csv.GetField(header), StringComparer.OrdinalIgnoreCase);
                vehicle = MapRow(row);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                vehicle = MapMalformedRow(csv.Context?.Parser?.Row ?? 0, exception.Message);
            }
            yield return vehicle;
        }
    }

    private static StreamReader CreateReader(Stream content) =>
        new(content, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true, bufferSize: 64 * 1024, leaveOpen: true);

    private static CsvReader CreateCsv(TextReader reader) =>
        new(reader, new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = true,
            TrimOptions = TrimOptions.Trim,
            DetectDelimiter = false,
            Delimiter = ",",
            BadDataFound = null,
            MissingFieldFound = args => throw new InvalidDataException($"Missing CSV field at row {args.Context?.Parser?.Row ?? 0}."),
            HeaderValidated = null
        });

    private static AuctionVehicle MapMalformedRow(long rowNumber, string message) => new()
    {
        Platform = InventorySourcePolicy.CopartExcelSource,
        LotNumber = $"invalid-row-{rowNumber}",
        TitleNotes = JsonSerializer.SerializeToElement(new Dictionary<string, string?> { ["csv_parse_error"] = message }),
        RawSource = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["source_row_number"] = rowNumber,
            ["csv_parse_error"] = message
        }),
        AdditionalData = new Dictionary<string, JsonElement>
        {
            ["source_row_kind"] = JsonSerializer.SerializeToElement("copart-csv-malformed")
        }
    };

    private static AuctionVehicle MapRow(IReadOnlyDictionary<string, string?> row)
    {
        var saleDate = Get(row, "Sale Date M/D/CY");
        var saleTime = Get(row, "Sale time (HHMM)");
        var timeZone = Get(row, "Time Zone");
        var titleType = Get(row, "Sale Title Type");
        var titleNotes = new Dictionary<string, string?>
        {
            ["sale_title_type"] = titleType,
            ["sale_title_state"] = Get(row, "Sale Title State")
        };
        var thumbnail = SafeMediaUrl(Get(row, "Image Thumbnail"));
        var image = SafeMediaUrl(Get(row, "Image URL"));
        var photos = new[] { thumbnail, image }.Where(static value => value is not null).Cast<string>().Distinct(StringComparer.Ordinal).ToArray();
        var primaryDamage = Get(row, "Damage Description");
        var secondaryDamage = Get(row, "Secondary Damage");
        var saleStatus = Get(row, "Sale Status");
        var raw = JsonSerializer.SerializeToElement(row);

        return new AuctionVehicle
        {
            Platform = InventorySourcePolicy.CopartExcelSource,
            LotNumber = Get(row, "Lot number"),
            Vin = Get(row, "VIN"),
            Title = titleType,
            Year = ParseInteger(Get(row, "Year")),
            Make = Get(row, "Make"),
            Model = FirstPresent(Get(row, "Model Detail"), Get(row, "Model Group")),
            VehicleType = Get(row, "Vehicle Type"),
            Color = Get(row, "Color"),
            FuelType = Get(row, "Fuel Type"),
            Transmission = Get(row, "Transmission"),
            DriveType = Get(row, "Drive"),
            VehicleSpecs = new VehicleSpecs
            {
                ExteriorColor = Get(row, "Color"),
                FuelType = Get(row, "Fuel Type"),
                Transmission = Get(row, "Transmission"),
                DriveType = Get(row, "Drive"),
                BodyStyle = Get(row, "Body Style"),
                Engine = Get(row, "Engine"),
                Cylinders = Get(row, "Cylinders"),
                Trim = Get(row, "Trim")
            },
            Condition = new VehicleCondition
            {
                PrimaryDamage = primaryDamage,
                SecondaryDamage = secondaryDamage,
                HasKey = ParseYesNo(Get(row, "Has Keys-Yes or No")),
                RunCondition = new RunConditionInfo { Value = NormalizeRunCondition(Get(row, "Runs/Drives")), Label = Get(row, "Runs/Drives") },
                LotConditionCode = Get(row, "Lot Cond. Code")
            },
            Facility = new AuctionFacility
            {
                Id = Get(row, "Yard number"),
                OfficeName = Get(row, "Yard name"),
                State = Get(row, "Location state"),
                Zip = Get(row, "Location ZIP")
            },
            Seller = new AuctionSeller { Name = Get(row, "Seller Name"), Type = null },
            OdometerInfo = new OdometerInfo { Miles = ParseDecimal(Get(row, "Odometer")), Status = Get(row, "Odometer Brand") },
            SaleDocument = new SaleDocument { Name = titleType, State = Get(row, "Sale Title State") },
            TitleNotes = JsonSerializer.SerializeToElement(titleNotes),
            SpecialNote = ToJson(Get(row, "Special Note")),
            Announcements = ToJson(Get(row, "Announcements")),
            Damage = primaryDamage,
            Auction = new AuctionInfo
            {
                State = saleStatus,
                AuctionAt = ParseAuctionDate(saleDate, saleTime, timeZone),
                LotStatus = saleStatus,
                LotSubStatus = Get(row, "Sale Light")
            },
            Pricing = new PricingInfo
            {
                CurrentBidUsd = ParseDecimal(Get(row, "High Bid =non-vix,Sealed=Vix")),
                BuyNowUsd = ParseDecimal(Get(row, "Buy-It-Now Price")),
                EstimatedRetailValueUsd = ParseDecimal(Get(row, "Est. Retail Value")),
                RepairCostUsd = ParseDecimal(Get(row, "Repair cost")),
                SalePriceUsd = null
            },
            Location = new VehicleLocation
            {
                Display = BuildLocationDisplay(Get(row, "Location city"), Get(row, "Location state")),
                State = Get(row, "Location state"),
                FacilityId = Get(row, "Yard number")
            },
            Media = new MediaInfo { ThumbnailsCount = photos.Length, Has360 = null, Photos = photos },
            RawSource = raw,
            AdditionalData = new Dictionary<string, JsonElement>
            {
                ["source_title_type_code"] = JsonSerializer.SerializeToElement(titleType),
                ["source_title_mapping"] = JsonSerializer.SerializeToElement("unmapped"),
                ["source_row_kind"] = JsonSerializer.SerializeToElement("copart-csv")
            }
        };
    }

    private static async Task<string> ComputeHashAsync(Stream content, CancellationToken cancellationToken)
    {
        content.Position = 0;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        int read;
        while ((read = await content.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            hash.AppendData(buffer, 0, read);
        content.Position = 0;
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static string? NormalizeHash(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) return null;
        var normalized = hash.Trim().ToLowerInvariant();
        return normalized.Length == 64 && normalized.All(static character => char.IsAsciiHexDigit(character)) ? normalized : null;
    }

    private static string? Get(IReadOnlyDictionary<string, string?> row, string name) =>
        row.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static string? FirstPresent(params string?[] values) => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static int? ParseInteger(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static decimal? ParseDecimal(string? value) => decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result) ? result : null;

    private static bool? ParseYesNo(string? value) => value?.Trim().ToUpperInvariant() switch
    {
        "YES" or "Y" or "TRUE" => true,
        "NO" or "N" or "FALSE" => false,
        _ => null
    };

    private static JsonElement? ToJson(string? value) => string.IsNullOrWhiteSpace(value) ? null : JsonSerializer.SerializeToElement(value);

    private static string? SafeMediaUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var candidate = value.Trim();
        if (!candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            candidate = $"https://{candidate.TrimStart('/')}";
        return Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.EndsWith("copart.com", StringComparison.OrdinalIgnoreCase)
            ? uri.GetLeftPart(UriPartial.Path)
            : null;
    }

    private static string? NormalizeRunCondition(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = value.Trim().ToUpperInvariant();
        return normalized.Contains("RUN & DRIVE", StringComparison.Ordinal) || normalized.Contains("RUNS AND DRIVES", StringComparison.Ordinal)
            ? "RUNS AND DRIVES"
            : value.Trim();
    }

    private static string? BuildLocationDisplay(string? city, string? state) =>
        string.IsNullOrWhiteSpace(city) ? state : string.IsNullOrWhiteSpace(state) ? city : $"{city} ({state})";

    private static DateTimeOffset? ParseAuctionDate(string? date, string? time, string? zone)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        var formats = new[] { "yyyyMMdd", "yyyy-MM-dd", "M/d/yyyy", "MM/dd/yyyy" };
        if (!DateTime.TryParseExact(date.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsedDate)) return null;

        var parsedTime = TimeOnly.MinValue;
        if (!string.IsNullOrWhiteSpace(time) && time.Trim() != "0")
        {
            var normalized = time.Trim().PadLeft(4, '0');
            if (!TimeOnly.TryParseExact(normalized, "HHmm", CultureInfo.InvariantCulture, DateTimeStyles.None, out parsedTime)) return null;
        }

        var offset = zone?.Trim().ToUpperInvariant() switch
        {
            "EDT" => TimeSpan.FromHours(-4),
            "EST" => TimeSpan.FromHours(-5),
            "CDT" => TimeSpan.FromHours(-5),
            "CST" => TimeSpan.FromHours(-6),
            "MDT" => TimeSpan.FromHours(-6),
            "MST" => TimeSpan.FromHours(-7),
            "PDT" => TimeSpan.FromHours(-7),
            "PST" => TimeSpan.FromHours(-8),
            _ => TimeSpan.Zero
        };
        return new DateTimeOffset(parsedDate.Date + parsedTime.ToTimeSpan(), offset);
    }
}
