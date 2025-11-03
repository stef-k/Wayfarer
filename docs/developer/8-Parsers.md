# Parsers (Imports)

Factory & Interfaces
- `Parsers/LocationDataParserFactory` produces a parser by file type.
- `Parsers/ILocationDataParser` defines `ParseAsync(Stream, userId)` -> `List<Location>`.

Supported Parsers
- `GpxLocationParser` — GPX tracks/waypoints.
- `KmlLocationParser`, `WayfarerKmlParser`, `GoogleMyMapsKmlParser` — KML variants.
- `CsvLocationParser` — CSV with lat/lon and timestamp headers.
- `WayfarerGeoJsonParser` — GeoJSON features.
- `GoogleTimelineJsonParser` — Google Location History exports.

Activity Types
- `LocationImportService` maps `ImportedActivityName` to existing `ActivityType` IDs when possible.

Geometry Handling
- Points: sanitized and stored as `geography(Point, 4326)`.
- Areas/Segments: SRID normalized to 4326 when missing.

Error Handling
- Batch processing; failures mark `LocationImport.Status` and log a truncated error message for diagnosis.

