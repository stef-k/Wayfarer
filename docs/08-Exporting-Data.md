# Exporting Data

Location Timeline Exports
- From the All Locations page, export your timeline to:
  - GeoJSON — includes reverse‑geocoded fields and metadata (accuracy, speed, altitude, heading, source)
  - CSV — flat table with rich address metadata, timestamps, and all capture metadata fields
  - GPX — track with Wayfarer extensions in `<extensions>` (address, activity, notes, metadata)
  - KML — placemarks with extended data including capture metadata

Metadata Preservation
All export formats now include location capture metadata when available:
- **Accuracy** — GPS accuracy in meters at time of recording
- **Speed** — movement speed
- **Altitude** — elevation above sea level
- **Heading** — compass bearing
- **Source** — origin identifier (e.g., "mobile", "import", "api")

This enables full roundtrip: export from Wayfarer, then reimport without losing data.

Trip Exports
- KML (Wayfarer flavor) — retains trip structure and colors for re‑import or viewing.
- KML (Google MyMaps) — compatible with Google MyMaps.
- PDF Guide — printable guide of your trip with the following features:
  - **Clickable place names** — Opens Google search for the location
  - **Clickable coordinates** — Opens Google Maps at the exact coordinates
  - Map snapshots for trip overview, regions, places, and route segments
  - Complete trip details including notes, travel modes, and distances
  - **Cancel button** — Stop PDF generation at any time during processing

Notes
- Export filenames include the current date/time for convenience.
- Exports contain only your own data and respect your trip privacy settings.

