# Importing & Exporting Data

## Offline queue recovery

Queue means pending mobile delivery, Timeline means phone-local history, and Wayfarer means confirmed server history. A recovery export never clears the queue.

Full recovery needs two manual imports: prepare recovery on the original phone (suspend delivery and wait for active work), export eligible rows, import the file into the replacement phone's Timeline, import the same file into Wayfarer, verify both independently, then **Resume and reconcile** if the original queue remains. Timeline import does not update Wayfarer or recreate queue state; Wayfarer import does not populate the phone Timeline.

For expedited synchronization use **Prepare/suspend → Export → Import into Wayfarer → Resume and reconcile**. Let import reach a terminal result first. Authenticated per-user GUID identity reuses already imported rows; missing rows upload normally. Partial/failed import must be inspected or retried, never followed by queue clearing. Confirmed queue rows become synced and follow ordinary retention.

CSV uses the CSV importer and suits spreadsheets/Python. GeoJSON uses the Wayfarer GeoJSON importer and suits GIS tools. Both carry the portable GUID; editing/removing it can prevent exact reconciliation. Files can contain precise positions/times, Notes, activity/check-in data, device/app/OS/provider/battery metadata, queue diagnostics, and identifiers. Store and transfer them securely, retain them until both histories are verified, then delete unnecessary copies.

---

## Importing Data

### Supported Formats

- **GPX** — GPS tracks and waypoints
- **KML** — Google Earth/Maps format
- **CSV** — Tabular points with lat/lon (headers required)
- **Wayfarer GeoJSON** — Wayfarer location-history exports; arbitrary generic GeoJSON is rejected
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

![Upload Import Dialog](images/upload-location-import-dialog.JPG)

![Import History](images/location-imports.JPG)

### Resumable Reverse Geocoding (Optional)

- Configure an authorized and verified personal provider profile before address enrichment. Imports share its remaining guard allowance and preserve retryable source data on exhaustion; see [Personal Location Providers](24-Personal-Location-Providers.md).
- Without a token, imports still work; address fields stay blank.
- Opt in during upload or use **Start** later. Import completion covers parsing, duplicate filtering, and insertion; enrichment can continue independently for days.
- Controls are **Start**, **Pause**, **Resume**, **Cancel**, and **Retry deferred**. Retry deferred explicitly overrides poison/no-result deferral under current authority without resetting usage or successes.
- Each Quartz execution contacts at most 100 wholly empty owned candidates in timestamp/ID order. Permanent and not-yet-due attempts are skipped so poison rows cannot starve later Locations.
- Geoapify geocoding and routing share a rolling pool and wake after the oldest counted admission expires plus five seconds. Mapbox Permanent Geocoding uses the next Wayfarer UTC month boundary plus five seconds.
- At the default 2,500-credit guard, 100,000 contacts need 1,000 executions and at least 40 windows—about 39 elapsed days before competition, retries, downtime, and latency.
- Deleting import history removes only its metadata/file. Locations, enrichment, workflow state, attempts, credentials, and usage remain. Trip imports stay separate and are not rerouted.

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
# Native and generic trip compatibility

Wayfarer-native KML schema v2 preserves ordered From/Via/To Place identity, waypoint route indices, custom-versus-fallback route state, transport profile, effective measurement, and explicit Automatic/Manual duration provenance. Native imports validate the complete aggregate before applying it, and creating a new trip remaps every Place identity consistently, including one shared identity for both endpoints of a closed loop. The same complete remapping is used by both trip-clone entry points.

Legacy Wayfarer KML v1 remains supported. Its `DurationMin` value is treated as an intentional Manual duration; absence defaults to Automatic. Imported distance is recalculated by the server from the effective route, and existing public/export duration minutes continue to use `TimeSpan.TotalMinutes`, so whole-second values retain fractional-minute precision.

Generic KML and GeoJSON remain geometry-only interchange formats. They do not infer semantic saved-Place waypoints, and generic route coordinates are imported exactly without dense-route simplification. Dense generic-route simplification is deferred to #425; external route generation is deferred to #426.
Imports with supplied addresses retain those values with unknown provenance. Missing-address enrichment is optional and uses the shared admitted persistent-provider boundary; unavailable providers do not stop accepted imports. There is no automatic enrichment queue. The authenticated Geoapify action processes at most 100 wholly unenriched owned Locations chronologically per invocation and resumes from remaining domain state.
