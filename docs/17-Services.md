# Services, Parsers & Jobs

## Location recovery convergence

`LocationImportService` and normal mobile upload converge through authenticated per-user `IdempotencyKey` uniqueness. The first stores the location; the second reuses/skips it and returns the same server identity. Legacy keyless imports retain timestamp/coordinate deduplication. Partial batches can contain stored and failed records, so review the terminal result before mobile resume. Server import changes Wayfarer history only and never restores or clears mobile queue state.

This document covers the key services, file parsers, and background jobs in the Wayfarer application.

---

## Location Services

### LocationService
- Core location data access and queries.
- Handles location CRUD operations with spatial queries.
- Integrates with reverse geocoding when available.
- **Key File**: `Services/LocationService.cs`

### LocationImportService
- Incrementally parses uploaded Google Timeline JSON, Wayfarer GeoJSON, CSV, GPX, and location-history KML files.
- Deduplicates and commits Location batches and durable progress independently of enrichment.
- Reconciles opted-in blank rows into the separate scheduled enrichment workflow without contacting a provider.
- Persists `Location` records to database.
- Publishes SSE progress updates during import.
- **Key File**: `Services/LocationImportService.cs`

### LocationStatsService
- Calculates user location statistics and analytics.
- Provides visit counts by country, region, city.
- Generates journey statistics and summaries.
- **Key File**: `Services/LocationStatsService.cs`

### ReverseGeocodingService
- Owns persistent reverse enrichment: protected authority, explicit Mapbox Permanent consent and verification, meter admission, `permanent=true`, bounded provider handling, generation revalidation, normalization, and provenance. Callers supply only authenticated user identity, coordinates, and intent.
- Personal credentials and provider-native admission are owned by the protected provider foundation; legacy `ApiToken` Mapbox rows migrate non-destructively. See [Personal Location Providers](24-Personal-Location-Providers.md).

Geoapify reverse geocoding and routing use separate cohesive adapters with fixed official endpoints, response/timeout bounds, exact normalization, and exception containment. Routing accepts only an explicit mode from Geoapify's closed catalog before shared-credit admission; Transport Profile metadata never selects a provider mode. Mobile discovery performs one protected-readability check, revalidates personal-authority generations, and hashes only non-secret authority fields. It never contacts a provider or enters admission/pacing. Mobile routing fences discovery-aware requests before admission and returns validated routes without persisting ad-hoc server state.
- Populates street, city, country, postal code fields.
- **Key File**: `Services/ReverseGeocodingService.cs`

---

## Trip Services

### TripExportService
- Exports trips to PDF and KML formats.
- PDF generation uses **Microsoft Playwright** for browser automation.
- Generated PDFs include:
  - Clickable place names → Google search
  - Coordinates → Google Maps links
  - Map snapshots via `MapSnapshotService`
- Supports cancellation via `CancellationToken`.
- SSE progress updates during generation.
- **Key File**: `Services/TripExportService.cs`

### TripImportService
- Imports trips from KML files (Google MyMaps or Wayfarer format).
- Handles duplicate detection with configurable modes.
- Parses regions, places, areas, and segments from KML.
- **Key File**: `Services/TripImportService.cs`

### TripWayfarerKmlExporter
- Exports trips to Wayfarer-specific KML format.
- Preserves all trip metadata for reimport.
- **Key File**: `Services/TripWayfarerKmlExporter.cs`

### TripTagService
- Manages trip tags with case-insensitive matching.
- Handles tag creation, assignment, and removal.
- Supports browsing public trips by tag.
- **Key File**: `Services/TripTagService.cs`

### TripThumbnailService
- Manages trip preview thumbnail lifecycle.
- Coordinates thumbnail generation and caching.
- **Key File**: `Services/TripThumbnailService.cs`

### TripMapThumbnailGenerator
- Generates map thumbnail images for trips.
- Uses Playwright to capture map screenshots.
- **Key File**: `Services/TripMapThumbnailGenerator.cs`

### MapSnapshotService
- Captures map screenshots using Playwright's Chromium.
- Manages Playwright browser installation and caching.
- Handles image proxying for Google My Maps integration.
- Cross-platform support (Windows, Linux x64/ARM64, macOS).
- **Key File**: `Services/MapSnapshotService.cs`

---

## Visit Detection Services

### PlaceVisitDetectionService
- Core visit detection logic for automatic place visits.
- Uses PostGIS spatial queries (`ST_DWithin`) with GiST indexes.
- **Two-hit candidate confirmation** reduces false positives from GPS noise.
- Creates `PlaceVisitEvent` records with snapshot data (place name, location, notes) preserved even if the place is later deleted.
- Event-driven cleanup of stale visits and candidates on each ping.
- **Key File**: `Services/PlaceVisitDetectionService.cs`

### VisitBackfillService
- Analyzes existing location history to create visits retroactively.
- **Tier-based detection**: Tier 1 (place radius), Tier 2 (2× radius), Tier 3 (configurable multiplier).
- **Consider Also suggestions** for near-miss visits using cross-tier evidence.
- Confidence scoring based on location count and proximity.
- Stale visit detection when places are deleted or moved.
- Chunked spatial queries for large datasets (>10,000 places).
- **Key File**: `Services/VisitBackfillService.cs`

---

## Group Services

### GroupService
- Core group management (create, update, delete).
- Handles member addition/removal with roles.
- Manages group settings and visibility.
- **Key File**: `Services/GroupService.cs`

### GroupTimelineService
- Group timeline queries with access control.
- Returns member locations respecting privacy settings.
- Integrates with SSE for real-time updates.
- **Key File**: `Services/GroupTimelineService.cs`

### GroupTimelineAccessContext
- Encapsulates access context for group timeline queries.
- Tracks requesting user, group membership, and permissions.
- **Key File**: `Services/GroupTimelineAccessContext.cs`

### InvitationService
- Manages group invitations with token-based acceptance.
- Handles invitation creation, acceptance, and expiry.
- Tracks invitation status (Pending, Accepted, Declined, Expired).
- **Key File**: `Services/InvitationService.cs`

---

## Real-Time Services

### SseService
- Server-Sent Events for real-time notifications.
- Channels for: locations, groups, visits, jobs, invitations, memberships.
- Manages client connections and message routing.
- **Key File**: `Services/SseService.cs`

---

## Map & Tile Services

### TileCacheService
- Manages provider-isolated map tile caching and bounded foreground/background scheduling.
- Built-in profiles use Wayfarer operational defaults of 6 upstream contacts/second,
  Interactive bypasses proactive contact-rate, burst, and client-series admission while retaining concurrency 6.
  Conservative uses Wayfarer safeguards of 12 actual contacts/second, burst 40, concurrency 8, and 480 admitted series/client/minute.
  These values are not provider-published guarantees or allowances.
- OSM Standard is best-effort with no SLA; prefetch, offline download, adjacent-zoom warming,
  and other cache-warming traffic are not permitted.
- Custom-provider administrators are responsible for configuring advanced limits according to
  their provider agreement.
- Provider and Phase 3 cache migration remain automatic. Rate-policy changes do not alter cache
  identity and do not require manually clearing cached tiles.
- LRU eviction for tiles at zoom levels ≥ 9.
- Permanent caching for zoom levels 0–8.
- Tracks metadata in database for cache management.
- Sends an honest `User-Agent` header identifying the application (OSM policy compliance) with a configurable contact email (`Application:ContactEmail`).
- Sets an origin-only `Referer` from the effective incoming request after its public hostname is authorized by `AllowedHosts`, preserving trusted reverse-proxy and Cloudflare Tunnel forwarding without exposing routes or request data.
- **Key File**: `Services/TileCacheService.cs`

---

## Application Services

### ApplicationSettingsService
- Centralized runtime settings management.
- In-memory caching with change notifications.
- Provides all visit detection thresholds and limits.
- **Key File**: `Services/ApplicationSettingsService.cs`

### RegistrationService
- Controls user registration based on `ApplicationSettings.IsRegistrationOpen`.
- Validates registration requests.
- **Key File**: `Services/RegistrationService.cs`

---

## Utility Services

### Wikipedia Utility (Frontend)
- Centralized Wikipedia search for location and trip place lookups.
- **Dual search strategy**: combines geosearch (coordinates) and text search (place name) for reliable article discovery.
- Provides popover integration with article summaries via Tippy.js.
- Reduces code duplication across 8+ view modules.
- **Key File**: `wwwroot/js/util/wikipedia-utils.js`

### RazorViewRenderer
- Renders Razor views to string.
- Used for PDF export HTML generation.
- **Key File**: `Services/RazorViewRenderer.cs`

### MobileCurrentUserAccessor
- Resolves current user in mobile/API contexts.
- Extracts user from API token authentication.
- **Key File**: `Services/MobileCurrentUserAccessor.cs`

### UserColorService
- Assigns consistent colors to users for map display.
- Used in group location sharing views.
- **Key File**: `Services/UserColorService.cs`

---

## Parsers (Location Imports)

### Factory & Interfaces

- `Parsers/LocationDataParserFactory` produces a parser by file type.
- `Parsers/ILocationDataParser` defines `ParseAsync(Stream, userId)` -> `List<Location>`.

### Supported Parsers

| Parser | Format |
|--------|--------|
| `GpxLocationParser` | GPX tracks/waypoints |
| `KmlLocationParser` | KML location data |
| `WayfarerKmlParser` | Wayfarer KML export format |
| `GoogleMyMapsKmlParser` | Google My Maps KML |
| `CsvLocationParser` | CSV with lat/lon and timestamp headers |
| `WayfarerGeoJsonParser` | GeoJSON features |
| `GoogleTimelineJsonParser` | Google Location History exports |

### Activity Types

- `LocationImportService` maps `ImportedActivityName` to existing `ActivityType` IDs when possible.

### Geometry Handling

- Points: sanitized and stored as `geography(Point, 4326)`.
- Areas/Segments: SRID normalized to 4326 when missing.

### Error Handling

- Batch processing failures mark `LocationImport.Status` and retain only a bounded product-safe error category.

---

## Background Jobs (Quartz.NET)

Wayfarer uses Quartz.NET for background job scheduling and execution.

### Scheduler Configuration

- Quartz configured with **ADO.NET job store** for persistence; see `ConfigureQuartz` in `Program.cs`.
- Jobs run in DI scopes via `ScopedJobFactory`.
- `JobExecutionListener` logs lifecycle events and records execution history.
- `qrtz_*` tables created automatically at startup if missing.
- Job type name migrations handled by `QuartzSchemaInstaller`.

### Built-In Jobs

#### LocationImportJob
- **Purpose**: Processes queued location imports in batches.
- **Trigger**: Scheduled when user uploads a file.
- **Features**:
  - Progress tracking via SSE.
  - Supports cancellation via `CancellationToken`.
  - Processes Google Timeline JSON, Wayfarer GeoJSON, CSV, GPX, and the existing location-history KML schema. Generic GeoJSON remains rejected.
- **Key File**: `Jobs/LocationImportJob.cs`

#### LocationEnrichmentJob

- Runs one user-owned batch of at most 10 candidates through the existing provider admission authority so the UI receives truthful intermediate committed checkpoints.
- Uses a stable durable job, epoch-bound one-shot trigger, non-replay misfire handling, and startup reconciliation. Relational state commits before Quartz projection, so scheduling failures remain repairable.
- Import parsing/insertion is separate and never checks provider authority, admits provider use, contacts a
  provider, enriches inline, or delays per record. Committed blank rows feed this scheduled workflow after
  each durable import batch.
- Long downtime causes one current-state execution, not replay of missed batches. Shutdown preserves committed Locations, admissions, attempts, counters, and intent.

#### VisitCleanupJob
- **Purpose**: Cleans up stale visit data globally.
- **Schedule**: Runs periodically (configurable).
- **Actions**:
  - Closes open visits with no pings beyond the configured threshold.
  - Deletes stale visit candidates that were never confirmed.
- **Settings Used**:
  - `VisitedEndVisitAfterMinutes` (derived from `LocationTimeThresholdMinutes × 9`)
  - `VisitedCandidateStaleMinutes` (derived from `LocationTimeThresholdMinutes × 12`)
- **Key File**: `Jobs/VisitCleanupJob.cs`

#### AuditLogCleanupJob
- **Purpose**: Removes audit log entries older than 2 years.
- **Schedule**: Runs periodically.
- **Supports**: Cancellation via `CancellationToken`.
- **Key File**: `Jobs/AuditLogCleanupJob.cs`

#### LogCleanupJob
- **Purpose**: Prunes application log files older than 1 month.
- **Schedule**: Runs periodically.
- **Supports**: Cancellation via `CancellationToken`.
- **Key File**: `Jobs/LogCleanupJob.cs`

### Job Status Tracking

All jobs update their status in `JobDataMap`:
- `Scheduled` — job is queued.
- `In Progress` — job is currently executing.
- `Completed` — job finished successfully.
- `Cancelled` — job was cancelled via cancellation token.
- `Failed` — job encountered an error.

Status messages provide additional details (e.g., "Deleted 5 old log files").

### Job Execution History

- `JobExecutionListener` records each job execution to the `JobHistories` table.
- Tracks: job name, status, last run time, error messages.
- Viewable in Admin > Jobs panel.

### Admin Job Control Panel

Located at **Admin > Jobs**, the control panel provides:

**Monitoring:**
- View all scheduled jobs with next fire time.
- See current status (Running, Paused, Scheduled).
- View last run time and status message.

**Controls:**
- **Pause** — temporarily stop a job's triggers.
- **Resume** — restart paused job triggers.
- **Cancel** — request cancellation of a running job (jobs must respect `CancellationToken`).
- **Trigger Now** — manually fire a job immediately.

**Real-Time Updates:**
- SSE stream (`/api/sse/stream/job-status`) pushes status changes.
- UI updates automatically without page refresh.

### Job Persistence

Quartz tables (`qrtz_*`) store:
- Job definitions and triggers.
- Cron expressions and schedules.
- Job data maps with status information.

Tables are created automatically on first startup if missing.

### Adding Custom Jobs

1. Create a class implementing `IJob`.
2. Inject dependencies via constructor (jobs run in DI scope).
3. Use `context.CancellationToken` for cancellation support.
4. Update `context.JobDetail.JobDataMap["Status"]` for monitoring.
5. Register in `Program.cs` within `ConfigureQuartz`.

Example:
```csharp
public class MyCustomJob : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var jobDataMap = context.JobDetail.JobDataMap;

        jobDataMap["Status"] = "In Progress";

        try
        {
            ct.ThrowIfCancellationRequested();
            // Do work...
            jobDataMap["Status"] = "Completed";
        }
        catch (OperationCanceledException)
        {
            jobDataMap["Status"] = "Cancelled";
        }
    }
}
```

---

## Options & Configuration

- `Models/Options/*` contains strongly-typed option classes.
- Options bound from `appsettings.json` configuration sections.
- Examples: `MobileSseOptions`, `TilesCacheOptions`.

---

## Uploads Pipeline

1. Files uploaded to `Uploads/Temp/` directory.
2. `LocationImportJob` scheduled to process file.
3. `LocationImportService` parses and persists data.
4. Progress updates sent via SSE.
5. Upload size limit enforced by `DynamicRequestSizeMiddleware`.
6. Limit configured via `ApplicationSettings.UploadSizeLimitMB`.

---

## External Route Generation

External route generation uses the authenticated user's verified and selected personal Geoapify directions authority. Web and updated-Mobile requests select an exact provider-native mode; the released-Mobile omitted-mode adapter accepts only the reviewed built-in stable keys. Unknown, case-varied, whitespace-varied, localized, or free-form keys fail before provider contact.

Fixed endpoint, request, parsing, timeout, response-size, pacing, concurrency, attribution, persistence, and cost rules are application policy. Personal credentials are protected by the persisted Data Protection key ring. Shared credit admission occurs before each outbound contact, so admitted timeouts and invalid responses remain conservatively counted. The five-operations-per-user-per-minute generation window remains separate from provider admission.

