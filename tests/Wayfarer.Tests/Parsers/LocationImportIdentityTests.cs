using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>Exercises file-scoped UUID validation on serialized native history inputs.</summary>
public sealed class LocationImportIdentityTests
{
    /// <summary>Optional values, accepted GUID spellings and one historical zero retain their identity on every pass.</summary>
    [Theory]
    [InlineData(LocationImportFileType.Csv)]
    [InlineData(LocationImportFileType.WayfarerGeoJson)]
    public async Task OptionalAndValidKeys_PreserveIdentityOnRepeatedEnumeration(LocationImportFileType format)
    {
        var key = Guid.NewGuid();
        object?[] keys = [null, "", " \t ", key.ToString("D"), key.ToString("B"), Guid.Empty.ToString("D")];
        var parser = new LocationDataParserFactory(NullLoggerFactory.Instance).GetParser(format);
        for (var pass = 0; pass < 2; pass++)
        {
            using var stream = Input(format, keys);
            var rows = await parser.ParseAsync(stream, "owner");
            Assert.Equal(new Guid?[] { null, null, null, key, key, Guid.Empty }, rows.Select(row => row.IdempotencyKey));
            Assert.All(rows, row => Assert.Equal("owner", row.UserId));
        }
    }

    /// <summary>A supplied invalid identity fails even when the row would otherwise be filtered.</summary>
    [Theory]
    [InlineData(LocationImportFileType.Csv, "none")]
    [InlineData(LocationImportFileType.Csv, "coordinate")]
    [InlineData(LocationImportFileType.Csv, "timestamp")]
    [InlineData(LocationImportFileType.WayfarerGeoJson, "none")]
    [InlineData(LocationImportFileType.WayfarerGeoJson, "coordinate")]
    [InlineData(LocationImportFileType.WayfarerGeoJson, "timestamp")]
    [InlineData(LocationImportFileType.WayfarerGeoJson, "geometry")]
    public async Task MalformedKey_FailsBeforeRowFiltering(LocationImportFileType format, string filter)
    {
        using var stream = Input(format, ["private-malformed-key"], filter);
        var parser = new LocationDataParserFactory(NullLoggerFactory.Instance).GetParser(format);
        var failure = await Assert.ThrowsAnyAsync<FormatException>(async () => await parser.ParseAsync(stream, "owner"));
        Assert.Equal("Invalid IdempotencyKey. Correct UUID values and upload the corrected file as a new import.", failure.Message);
    }

    /// <summary>Two zero occurrences fail regardless of payload equality or filters on the first occurrence.</summary>
    [Theory]
    [InlineData(LocationImportFileType.Csv, "none")]
    [InlineData(LocationImportFileType.Csv, "coordinate")]
    [InlineData(LocationImportFileType.Csv, "timestamp")]
    [InlineData(LocationImportFileType.WayfarerGeoJson, "none")]
    [InlineData(LocationImportFileType.WayfarerGeoJson, "coordinate")]
    [InlineData(LocationImportFileType.WayfarerGeoJson, "timestamp")]
    [InlineData(LocationImportFileType.WayfarerGeoJson, "geometry")]
    public async Task SecondZero_FailsBeforeRowFiltering(LocationImportFileType format, string filter)
    {
        using var stream = Input(format, [Guid.Empty.ToString("D"), Guid.Empty.ToString("B")], filter);
        var parser = new LocationDataParserFactory(NullLoggerFactory.Instance).GetParser(format);
        var failure = await Assert.ThrowsAnyAsync<FormatException>(async () => await parser.ParseAsync(stream, "owner"));
        Assert.Equal("Multiple zero IdempotencyKeys. Give distinct captures unique nonzero UUIDs and upload the corrected file as a new import.", failure.Message);
    }

    /// <summary>Raw GeoJSON types cannot be coerced into optional identity strings.</summary>
    [Theory]
    [InlineData("17")]
    [InlineData("false")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("[\"00000000-0000-0000-0000-000000000000\"]")]
    public async Task GeoJson_NonstringKeyFails(string rawKey)
    {
        using var key = JsonDocument.Parse(rawKey);
        using var stream = Input(LocationImportFileType.WayfarerGeoJson, [key.RootElement]);
        var parser = new WayfarerGeoJsonParser(NullLogger<WayfarerGeoJsonParser>.Instance);
        var failure = await Assert.ThrowsAnyAsync<FormatException>(async () => await parser.ParseAsync(stream, "owner"));
        Assert.StartsWith("Invalid IdempotencyKey.", failure.Message);
    }

    /// <summary>Builds real native rows; zero witnesses deliberately share the same payload.</summary>
    private static MemoryStream Input(LocationImportFileType format, object?[] keys, string filter = "none")
    {
        var payload = format == LocationImportFileType.Csv
            ? "Latitude,Longitude,TimestampUtc,IdempotencyKey\r\n" + string.Join("\r\n", keys.Select((key, index) =>
                $"{(index == 0 && filter == "coordinate" ? "100" : "40.1")},22.2,{(index == 0 && filter == "timestamp" ? "invalid" : "2026-08-25T00:00:00Z")},{key}"))
            : JsonSerializer.Serialize(new
            {
                type = "FeatureCollection",
                features = keys.Select((key, index) => new
                {
                    type = "Feature",
                    geometry = index == 0 && filter == "geometry" ? null : new
                    {
                        type = "Point", coordinates = new[] { 22.2, index == 0 && filter == "coordinate" ? 100 : 40.1 }
                    },
                    properties = new
                    {
                        TimestampUtc = index == 0 && filter == "timestamp" ? "invalid" : "2026-08-25T00:00:00Z",
                        IdempotencyKey = key
                    }
                })
            });
        return new MemoryStream(Encoding.UTF8.GetBytes(payload));
    }
}
