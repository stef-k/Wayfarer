# Services

Selected Services and Responsibilities
- `LocationImportService` — batch parses uploads, optional reverse geocoding, persists `Location`s, publishes SSE progress.
- `TripExportService` + `TripWayfarerKmlExporter` — builds KML and PDF guides from `Trip` data.
- `ReverseGeocodingService` — enriches points when an API key is available (key managed via per‑user `ApiToken` entries).
  - Per‑user Mapbox token: store as `ApiToken` with name "Mapbox" on the user; when present, imports and manual add flows call the API and populate address fields.
- `TileCacheService` — manages map tile caching and metadata.
- `SseService` — server‑sent events for progress/notifications (e.g., imports).
- `RegistrationService` — registration controls, depending on `ApplicationSettings.IsRegistrationOpen`.
- `LocationService`, `LocationStatsService` — location data access and analytics.
- `GroupService`, `GroupTimelineService` — groups, access context, and timeline queries.
- `RazorViewRenderer` — render Razor views to string (e.g., PDF export).
- `MobileCurrentUserAccessor` — resolves current user in mobile/API contexts.

Options & Settings
- `Models.Options.*` and `ApplicationSettingsService` centralize runtime settings and change notifications.

Uploads Pipeline
- Uploads are staged under `Uploads/Temp/` and processed by `LocationImportJob` -> `LocationImportService`.
- Upload size limit is controlled in DB via `ApplicationSettings.UploadSizeLimitMB` and enforced by `DynamicRequestSizeMiddleware`.
