# Wayfarer Test Coverage Status & Next Steps (Backend-First)

_Date: 2025-11-21 • Scope: Backend only (controllers/services/jobs); JS deferred_

This refresh compares the current test suite against the archived **test-coverage-expansion-plan.md** and summarizes what is covered, what remains, and the next high-ROI test batch. All work continues in `tests/Wayfarer.Tests` only; production code stays untouched. Every new test must run clean (`dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj`) before commit.

---

## 1) Current Coverage Status

- **Suite size**: 462 passing tests across 67 C# files (xUnit + EFCore InMemory + Moq/fakes). Focus remains backend; JS/browser automation deliberately deferred.
- **New coverage since the archived plan** (high-value additions):
  - **Controllers**: Admin (ActivityType, ApiToken incl. regen/delete, AuditLogs, Jobs, Settings, Users), Manager (ApiToken, Groups incl. AJAX, Users incl. create/role checks), User (Location CRUD incl. bulk edit/preview, HiddenAreas), API (Location CRUD/bulk delete, Groups, Invitations).
  - **Services**: SseService timing, TripTagService, TripExport/Import refinements, ReverseGeocodingService, ApplicationSettingsService, Group/Invitation/Location/LocationStats, TripThumbnail, MobileCurrentUserAccessor, RegistrationService.
  - **Parsers**: CSV, GPX, KML (Wayfarer + MyMaps), GeoJSON, Google Timeline, TripWayfarerKmlExporter, Parser factory.
  - **Integration**: Location SSE broadcasts, Group member listing/auto-delete, org peer visibility, mobile flows, group locations API.
- **CRUD coverage**: Trip/User/Location creation/edit/delete flows now exercised for Admin/Manager/User/API surfaces where implemented; gaps remain for User Trip import/export CRUD helpers, Public tiles/viewers, and ancillary User controllers (regions/places/segments, timeline settings).
- **Untested / partial areas** (highest risk first):
  - **User area controllers**: AreasController, ApiTokenController, LocationImportController, LocationExportController, LocationImport/Export flows, Places/Regions/Segments controllers, SettingsController, TimelineController, TripImportController, public-trip toggles inside TripController, LocationExportController.
  - **Public area**: TripViewerController, UsersTimelineController, TilesController (incl. referer checks), TagsController.
  - **API area residuals**: ActivityController, IconsController, SettingsController, TagsController, TripsController, UsersController (non-mobile paths).
  - **Services**: LocationImportService, MapSnapshotService, TileCacheService, TripMapThumbnailGenerator, QuartzHostedService, RazorViewRenderer, ApiTokenService helpers, CoordinateTimeZoneConverter edge cases.
  - **Jobs/Middleware**: LocationImportJob, LogCleanupJob, AuditLogCleanupJob, JobExecutionListener/JobFactory/ScopedJobFactory, QuartzHostedService lifecycle, DynamicRequestSizeMiddleware, PerformanceMonitoringMiddleware.
  - **Infra/IO**: Tile cache disk handling, map snapshot Playwright flow, Quartz scheduling semantics—all currently without automated coverage; rely on manual checks.
- **Constraints acknowledged**: No tests for 3rd-party middleware/libraries; PostGIS/Testcontainers/real Playwright browsers are deferred until higher-ROI in-memory coverage is landed.

---

## 2) Next Test Batch (Prioritized, Backend Only)

### A. User/Manager/Admin/Public Controllers – High ROI CRUD & Auth
- **Targets**: User (ApiTokenController, AreasController, Places/Regions/SegmentsController, LocationImportController, LocationExportController, SettingsController, TimelineController, TripImportController, remaining TripController branches), Public (TripViewer, UsersTimeline, Tiles, Tags), API (Activity, Icons, Settings, Tags, Trips, Users).
- **Why**: Closes remaining CRUD/auth gaps across all areas; catches validation/authorization regressions without heavy infra.
- **Approach**: Prefer `WebApplicationFactory<Program>` with seeded identities/roles; reuse existing fixtures; assert authorization, validation errors, success redirects, and payload shapes. Keep DB in-memory; no production code changes.

### B. Location Import Pipeline & SSE Progress (Logic-Focused)
- **Targets**: LocationImportService orchestration, LocationImportController start/stop/delete, LocationImportJob scheduling, SSE progress payloads, ReverseGeocodingService seams (cache/timeout/error paths).
- **Why**: High data-integrity/regression risk; currently untested beyond parsers.
- **Approach**: Fake `IReverseGeocodingService`/`SseService`; use in-memory Quartz or scheduler fakes; verify dedupe, date-range filtering, failure logging, and SSE contract. Defer PostGIS/Testcontainers until after in-memory coverage is solid.

### C. Trip Import/Export Surface (Controller + Helper Services)
- **Targets**: TripImportController, TripExportController, TripMapThumbnailGenerator, MapSnapshotService (logic only), TripThumbnailService edge cases.
- **Why**: Export/import are core flows; currently only service/unit coverage without controller-level regressions or snapshot/thumb branches.
- **Approach**: Controller tests via TestServer; stub Playwright/browser/filesystem with fakes; assert FileResult metadata, error handling, and fallback thumbnails. No real browser/IO yet.

### D. Public Tile Cache & TileCacheService
- **Targets**: TileCacheService (init/evict/concurrency), Public TilesController referer/credential handling.
- **Why**: Offline maps stability + public endpoint correctness; currently zero coverage.
- **Approach**: Temp directories + fake HttpClient responses; validate size accounting, LRU eviction, and 403/404 paths. Avoid touching real cache paths.

### E. Jobs, Hosted Services, Middleware (Wayfarer-authored only)
- **Targets**: AuditLogCleanupJob, LogCleanupJob, JobExecutionListener/JobFactory/ScopedJobFactory, QuartzHostedService start/stop, DynamicRequestSizeMiddleware, PerformanceMonitoringMiddleware.
- **Why**: Background cleanup and request shaping are unguarded; cheap to cover with in-memory data + TestServer.
- **Approach**: Use capturing loggers and seeded EF data; pipe mock requests through middleware in TestServer; assert retention windows and metric/log outputs.

---

### Specialized Setup & Mocks
- **Heavy deps deferred**: PostGIS/Testcontainers, real Playwright/browsers, external map tile calls—schedule only after Priorities A–D are green.
- **Mocking strategy**: Introduce/extend test fakes for reverse geocoding, SSE, filesystem/tile IO, headless snapshots, and scheduler abstractions. Keep DB operations on EF InMemory. Avoid touching 3rd-party middleware.

### Working Agreements
- Tests reside only in `tests/Wayfarer.Tests`; production code remains unchanged for this phase.
- Always run `dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj` before any commit to `feature/test-coverage-expansion`.
- Continue backend-first; JS/Leaflet/Quill tests remain a later iteration once backend coverage stabilizes.
