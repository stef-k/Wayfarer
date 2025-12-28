# Services

This document covers the key services in the Wayfarer application.

---

## Location Services

### LocationService
- Core location data access and queries.
- Handles location CRUD operations with spatial queries.
- Integrates with reverse geocoding when available.
- **Key File**: `Services/LocationService.cs`

### LocationImportService
- Batch parses uploaded location files (JSON, GPX, KML).
- Optional reverse geocoding during import.
- Persists `Location` records to database.
- Publishes SSE progress updates during import.
- **Key File**: `Services/LocationImportService.cs`

### LocationStatsService
- Calculates user location statistics and analytics.
- Provides visit counts by country, region, city.
- Generates journey statistics and summaries.
- **Key File**: `Services/LocationStatsService.cs`

### ReverseGeocodingService
- Enriches coordinates with address data via Mapbox API.
- Per-user Mapbox token stored as `ApiToken` with name "Mapbox".
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
- Manages OpenStreetMap tile caching.
- LRU eviction for tiles at zoom levels ≥ 9.
- Permanent caching for zoom levels 0–8.
- Tracks metadata in database for cache management.
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
