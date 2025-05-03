using Wayfarer.Models.Enums;

namespace Wayfarer.Parsers
{
    public class LocationDataParserFactory
    {
        private readonly ILogger<GoogleTimelineJsonParser> _logger;

        public LocationDataParserFactory(ILogger<GoogleTimelineJsonParser> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public ILocationDataParser GetParser(LocationImportFileType fileType)
        {
            return fileType switch
            {
                LocationImportFileType.GoogleTimeline => new GoogleTimelineJsonParser(_logger),
                // LocationImportFileType.Gpx => new GpxParser(),
                // LocationImportFileType.GeoJson => new GeoJsonParser(),
                // LocationImportFileType.Kml => new KmlParser(),
                // LocationImportFileType.Csv => new CsvParser(),
                _ => throw new NotSupportedException($"Unsupported import file type: {fileType}")
            };
        }
    }
}