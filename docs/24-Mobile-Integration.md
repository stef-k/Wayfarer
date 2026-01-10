# Mobile Integration

Overview
- Mobile app: .NET MAUI project (`Wayfarer.Mobile`) with Refit-based API clients, offline tile caching, optional background tracking, and SSE subscriptions for live updates.
- Server-agnostic: users configure their own server URL and API token; no hardcoded domains.

HTTP Clients
- Base URL comes from `SettingsStore.GetApiBaseUrl()` which appends `/api` to the configured `ServerUrl`.
- Refit interfaces:
  - `IWayfarerApiService` (base `/api/`):
    - `GET /settings` — fetch server thresholds (time/distance/accuracy) for logging guidance.
    - `GET /activity` — list activity types.
    - `POST /location/log-location` — log background location with filtering (time, distance, accuracy, duplicates).
    - `POST /location/check-in` — manual check-in with rate limits (bypasses time/distance thresholds).
  - `ITripContentApiService` (explicit `/api/trips/...`):
    - `GET /api/trips` — current user trips.
    - `GET /api/trips/{tripId}` — full trip content (public or owner).
    - `GET /api/trips/{tripId}/boundary` — bounding box for tile prefetch.
    - `POST /api/trips/{tripId}/tiles` — server-side tile list generation (if used by UI flows).

Mobile-Specific API (Groups & SSE)
- Group endpoints (Bearer token), used by `GroupLocationsService`:
  - `GET /api/mobile/groups/{groupId}/members` — group members.
  - `POST /api/mobile/groups/{groupId}/locations/latest` — latest points per member.
  - `POST /api/mobile/groups/{groupId}/locations/query` — bounding-box/time filtered query with pagination.
- SSE client (`SseLocationClient`):
  - `GET /api/mobile/sse/visits` — visit notifications for authenticated user.
  - `GET /api/mobile/sse/group/{groupId}` — consolidated group events (locations + membership).
  - Adds `Authorization: Bearer <token>` and `Accept: text/event-stream`.
  - Auto-reconnect with backoff (1s, 2s, 5s), heartbeat comments handled.

Visit Notifications
- **Real-time (foreground)**: Subscribe to `/api/mobile/sse/visits` for instant `visit_started` events when arriving at planned places.
- **Background polling (fallback)**: iOS/Android kill SSE connections when backgrounded. Poll `/api/mobile/visits/recent?since=30` after each location log to catch visits.
- **Recommended pattern**:
  1. `POST /api/location/log-location` — log GPS position
  2. If backgrounded: `GET /api/mobile/visits/recent?since=30` — poll for new visits
  3. Display local notification for any new visits
- **Response fields**: `visitId`, `tripId`, `tripName`, `placeId`, `placeName`, `regionName`, `arrivedAtUtc`, `latitude`, `longitude`, `iconName`, `markerColor`.

Auth & Configuration
- Token: per-user API token from the web app; stored in `SettingsStore.ApiToken`.
- Server URL: `SettingsStore.ServerUrl` (must be set by user). `ApiServiceManager` rebuilds clients on changes.
- QR Scanner: `QrScannerService` can parse a QR containing server config to populate URL and token.
- SSL (development): `WayfarerHttpService` conditionally bypasses SSL validation for localhost/private networks in DEBUG.

Tracking & Permissions
- `TrackingCoordinator` manages GPS permission prompts and background capability; platform-specific trackers implement `IBackgroundTracker`.
- Tracking is independent of GPS activation: GPS may run while timeline logging is disabled (user toggle).
- Manual check-in uses `/api/location/check-in` with rate limiting (30s min interval, 60/hour) and returns standard responses.

Offline Tiles & Caching
- Services: `LiveTileCacheService`, `TripTileCacheService`, `UnifiedTileCacheService`, with progress and notifications.
- Storage paths under `FileSystem.AppDataDirectory` (e.g., `tiles/trips`). Uses SQLite (`wayfarer.db`) to track downloads.
- Throttling: `TileRateLimiter`, `SettingsStore.MaxConcurrentTileDownloads` and `MinTileRequestDelayMs`.
- Tile server URL configurable via `SettingsStore.TileServerUrl` (defaults to OSM standard tile server). Respect provider usage policies.
- Server cache behaviour: the backend caches tiles for zoom levels 0-8 permanently and applies an LRU eviction policy for higher zooms. The default `CacheSettings:MaxCacheSizeMb` is 1024 MB but can be reduced for constrained hosts.
- Trip downloads coordinate with `TripContentService` which stores Trip/Region/Place/Area/Segment metadata locally.

Trip Content & Navigation
- `TripContentService` pulls `TripDto` and stores structured content; progress events include counts for places/areas/segments.
- Navigation stack: `NavigationGraphBuilder`, `RouteCalculationService`, `TripNavigationService`, optional `NavigationAudioService`.

DTOs & Conventions
- JSON: camelCase, UTC timestamps. Geometry SRID normalized to 4326 by backend.
- Selected DTOs (mobile side): `TripDto`, `BoundingBoxDto`, `TripTileRequestDto`, `TripTileListDto`, `TimelineLocationDto`, `GroupLocationsQueryRequest/Response`.

User Settings Surface (selected)
- `ServerUrl`, `ApiToken`, `TrackingEnabled`.
- Tile cache: `MaxLiveCacheMB`, `MaxTripCacheMB`, `LiveCachePrefetchRadius`, `PrefetchDistanceThresholdMeters`.
- Navigation: audio on/off, language, volume, distance units, auto-reroute.

Build & Platform Notes
- MAUI app configured via `MauiProgram.cs` (SkiaSharp, ZXing, Syncfusion).
- Android handler tweaks for Material button background tinting.
- Handlers: custom WebView handler permits external content where needed.

Best Practices
- Do not ship real tokens in code or screenshots. Provide QR onboarding for operator-specific servers.
- Respect tile server terms; consider self-hosted tiles for heavy usage.
- Keep SSE subscriptions scoped and stop on background when not needed to conserve battery.

