# Importing & Exporting Data

---

## Importing Data

### Supported Formats

- **GPX** — GPS tracks and waypoints
- **KML** — Google Earth/Maps format
- **CSV** — Tabular points with lat/lon (headers required)
- **GeoJSON** — Features and geometry collections
- **Google Timeline JSON** — Export from Google location history

### How Imports Work

- Upload a file via the import interface.
- A background job parses your file in batches.
- **Deduplication** — imports automatically detect and skip duplicate locations based on timestamp and coordinates within a small tolerance.
- **Metadata preservation** — accuracy, speed, altitude, heading, and source fields are imported when available.
- If reverse geocoding is configured (per-user token), missing addresses are enriched.
- Progress updates show status and last imported record.

### Import Controls

- **Start** — begin or resume processing a stopped/failed import.
- **Stop** — pause an in-progress import (can resume later).
- **Regenerate** — reprocess the file from scratch.
- **Delete** — remove the import and associated uploaded file.
- Status indicators: InProgress, Completed, Stopped, Failed, Stopping.
- Large files are processed asynchronously with SSE progress updates.

### Reverse Geocoding (Optional)

- Add a personal Mapbox API token to enrich imported points with addresses.
- Create an API token named "Mapbox" under your account, then re-run imports to enrich future data.
- Without a token, imports still work; address fields stay blank.

### Metadata Fields

All parsers support optional metadata fields when present in the source data:

- **Accuracy** — GPS accuracy in meters
- **Speed** — movement speed at time of recording
- **Altitude** — elevation above sea level
- **Heading** — compass bearing (0-360 degrees)
- **Source** — origin identifier for roundtrip compatibility

Format-specific field mappings:

| Format | Mappings |
|--------|----------|
| GPX | `<hdop>` → accuracy, `<speed>`, `<ele>` → altitude, `<course>` → heading |
| GeoJSON | `accuracy`, `speed`, `altitude`, `heading`, `source` properties |
| CSV | columns named `accuracy`, `speed`, `altitude`, `heading`, `source` |
| KML | Extended data elements with matching names |
| Google Timeline | `accuracy`, `velocity` → speed, `altitude`, `heading` |

### Tips for Clean Imports

- Ensure coordinates use WGS84 (EPSG:4326). Non-standard SRIDs are normalized.
- Include timestamps for timeline sorting.
- Provide activity type when possible; Wayfarer maps imported names to known types when it can.
- Include metadata fields for richer location records that survive export/reimport cycles.

### Troubleshooting Imports

- Stuck import: refresh the page; if it persists, contact your admin to check logs.
- Invalid file: confirm format and required columns/fields.
- Large files: your admin can adjust upload size limits in Admin Settings.

---

## Exporting Data

### Location Timeline Exports

From the All Locations page, export your timeline to:

- **GeoJSON** — includes reverse-geocoded fields and metadata (accuracy, speed, altitude, heading, source)
- **CSV** — flat table with rich address metadata, timestamps, and all capture metadata fields
- **GPX** — track with Wayfarer extensions in `<extensions>` (address, activity, notes, metadata)
- **KML** — placemarks with extended data including capture metadata

### Metadata Preservation

All export formats include location capture metadata when available:

- **Accuracy** — GPS accuracy in meters at time of recording
- **Speed** — movement speed
- **Altitude** — elevation above sea level
- **Heading** — compass bearing
- **Source** — origin identifier (e.g., "mobile", "import", "api")

This enables full roundtrip: export from Wayfarer, then reimport without losing data.

### Trip Exports

- **KML (Wayfarer flavor)** — retains trip structure and colors for re-import or viewing.
- **KML (Google MyMaps)** — compatible with Google MyMaps.
- **PDF Guide** — printable guide of your trip with:
  - Clickable place names → Google search
  - Clickable coordinates → Google Maps
  - Map snapshots for trip overview, regions, places, and route segments
  - Complete trip details including notes, travel modes, and distances
  - Cancel button to stop PDF generation at any time

### Notes

- Export filenames include the current date/time for convenience.
- Exports contain only your own data and respect your trip privacy settings.
