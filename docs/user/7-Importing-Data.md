# Importing Data

Supported Formats
- GPX — GPS tracks and waypoints.
- KML — Google Earth/Maps format.
- CSV — Tabular points with lat/lon (headers required).
- GeoJSON — Features and geometry collections.
- Google Timeline JSON — Export from Google location history.

How Imports Work
- Upload a file via the import interface.
- A background job parses your file in batches.
- If reverse geocoding is configured (per‑user token), missing addresses are enriched.
- Progress updates show status and last imported record.

Reverse Geocoding Tokens (Optional)
- You can add a personal Mapbox API token to enrich imported points with addresses.
- Create an API token named “Mapbox” under your account (or via Admin/Manager if delegated), then re‑run imports to enrich future data.
- Without a token, imports still work; address fields stay blank.

Tips for Clean Imports
- Ensure coordinates use WGS84 (EPSG:4326). Non‑standard SRIDs are normalized.
- Include timestamps for timeline sorting.
- Provide activity type when possible; Wayfarer maps imported names to known types when it can.

Troubleshooting Imports
- Stuck import: refresh the page; if it persists, contact your admin to check logs.
- Invalid file: confirm format and required columns/fields.
- Large files: your admin can adjust upload size limits in Admin Settings.

