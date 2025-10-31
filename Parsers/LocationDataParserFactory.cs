using Wayfarer.Models.Enums;

namespace Wayfarer.Parsers
{
    public class LocationDataParserFactory
    {
        private readonly ILoggerFactory _loggerFactory;

        public LocationDataParserFactory(ILoggerFactory loggerFactory)
        {
            _loggerFactory = loggerFactory 
                             ?? throw new ArgumentNullException(nameof(loggerFactory));
        }

        public ILocationDataParser GetParser(LocationImportFileType fileType)
        {
            return fileType switch
            {
                LocationImportFileType.GoogleTimeline => new GoogleTimelineJsonParser(_loggerFactory.CreateLogger<GoogleTimelineJsonParser>()),
                LocationImportFileType.WayfarerGeoJson => new WayfarerGeoJsonParser(_loggerFactory.CreateLogger<WayfarerGeoJsonParser>()),
                LocationImportFileType.Gpx => new GpxLocationParser(_loggerFactory.CreateLogger<GpxLocationParser>()),
                // LocationImportFileType.GeoJson => new GeoJsonParser(),
                LocationImportFileType.Kml => new KmlLocationParser(_loggerFactory.CreateLogger<KmlLocationParser>()),
                LocationImportFileType.Csv => new CsvLocationParser(_loggerFactory.CreateLogger<CsvLocationParser>()),
                _ => throw new NotSupportedException($"Unsupported import file type: {fileType}")
            };
        }
    }
}
