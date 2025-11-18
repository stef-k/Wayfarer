using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Parsers;

/// <summary>
/// Tests for the LocationDataParserFactory which creates appropriate parsers based on file type.
/// </summary>
public class LocationDataParserFactoryTests
{
    private readonly LocationDataParserFactory _factory;

    public LocationDataParserFactoryTests()
    {
        _factory = new LocationDataParserFactory(NullLoggerFactory.Instance);
    }

    [Fact]
    public void GetParser_GoogleTimeline_ReturnsGoogleTimelineJsonParser()
    {
        // Act
        var parser = _factory.GetParser(LocationImportFileType.GoogleTimeline);

        // Assert
        Assert.IsType<GoogleTimelineJsonParser>(parser);
    }

    [Fact]
    public void GetParser_Csv_ReturnsCsvLocationParser()
    {
        // Act
        var parser = _factory.GetParser(LocationImportFileType.Csv);

        // Assert
        Assert.IsType<CsvLocationParser>(parser);
    }

    [Fact]
    public void GetParser_Gpx_ReturnsGpxLocationParser()
    {
        // Act
        var parser = _factory.GetParser(LocationImportFileType.Gpx);

        // Assert
        Assert.IsType<GpxLocationParser>(parser);
    }

    [Fact]
    public void GetParser_Kml_ReturnsKmlLocationParser()
    {
        // Act
        var parser = _factory.GetParser(LocationImportFileType.Kml);

        // Assert
        Assert.IsType<KmlLocationParser>(parser);
    }

    [Fact]
    public void GetParser_WayfarerGeoJson_ReturnsWayfarerGeoJsonParser()
    {
        // Act
        var parser = _factory.GetParser(LocationImportFileType.WayfarerGeoJson);

        // Assert
        Assert.IsType<WayfarerGeoJsonParser>(parser);
    }

    [Fact]
    public void GetParser_UnsupportedType_ThrowsNotSupportedException()
    {
        // Arrange - Invalid enum value
        var invalidType = (LocationImportFileType)999;

        // Act & Assert
        Assert.Throws<NotSupportedException>(() => _factory.GetParser(invalidType));
    }

    [Fact]
    public void Constructor_NullLoggerFactory_ThrowsArgumentNullException()
    {
        // Act & Assert
        Assert.Throws<ArgumentNullException>(() => new LocationDataParserFactory(null!));
    }

    [Fact]
    public void GetParser_AllSupportedTypes_ReturnValidParsers()
    {
        // Arrange
        var supportedTypes = new[]
        {
            LocationImportFileType.GoogleTimeline,
            LocationImportFileType.Csv,
            LocationImportFileType.Gpx,
            LocationImportFileType.Kml,
            LocationImportFileType.WayfarerGeoJson
        };

        // Act & Assert
        foreach (var fileType in supportedTypes)
        {
            var parser = _factory.GetParser(fileType);
            Assert.NotNull(parser);
            Assert.IsAssignableFrom<ILocationDataParser>(parser);
        }
    }
}
