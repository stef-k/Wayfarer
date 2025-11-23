using System.Linq;
using Wayfarer.Models.Enums;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>
/// Location import file type extension helpers.
/// </summary>
public class LocationImportFileTypeExtensionsTests
{
    [Theory]
    [InlineData(LocationImportFileType.GoogleTimeline, ".json")]
    [InlineData(LocationImportFileType.WayfarerGeoJson, ".geojson")]
    [InlineData(LocationImportFileType.Gpx, ".gpx")]
    [InlineData(LocationImportFileType.GeoJson, ".json")]
    [InlineData(LocationImportFileType.Kml, ".kml")]
    [InlineData(LocationImportFileType.Csv, ".csv")]
    public void GetAllowedExtensions_ReturnsExpected(LocationImportFileType type, string expected)
    {
        var extensions = type.GetAllowedExtensions().ToList();

        Assert.Contains(expected, extensions);
    }

    [Fact]
    public void IsExtensionValid_IsCaseInsensitive()
    {
        var result = LocationImportFileType.WayfarerGeoJson.IsExtensionValid(".GEOJSON");

        Assert.True(result);
    }

    [Fact]
    public void GetAllowedExtensions_ReturnsEmpty_ForUnknownType()
    {
        const LocationImportFileType unknown = (LocationImportFileType)999;

        var extensions = unknown.GetAllowedExtensions();

        Assert.Empty(extensions);
    }

    [Fact]
    public void IsExtensionValid_ReturnsFalse_ForUnknownExtension()
    {
        var result = LocationImportFileType.Csv.IsExtensionValid(".kml");

        Assert.False(result);
    }
}
