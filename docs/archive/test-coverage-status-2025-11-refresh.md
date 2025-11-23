# Wayfarer Test Coverage Status & Next Steps (Backend-First)

_Date: 2025-11-22 | Scope: Backend only (controllers/services/jobs); JS deferred_

This refresh compares the current test suite against the archived **test-coverage-expansion-plan.md** and summarizes what is covered, what remains, and the next high-ROI test batch. All work continues in `tests/Wayfarer.Tests` only; production code stays untouched. Every new test must run clean (`dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj`) before commit.

---

## 1) Current Coverage Status

- **Suite size**: 596 passing across 70+ C# test files. Backend-only; JS/browser automation remains deferred.
- **Coverage snapshot (views excluded)**: 18.30% line / 38.26% branch. `tools/coverage-report.ps1` excludes `AspNetCoreGeneratedDocument*` Razor artifacts plus migrations, Identity pages (except our customized Register flow), and applies report-level file filters to drop DTOs/ViewModels so the report focuses on executable backend logic. HTML lives at `coverage-report/index.html` (XML at `tests/Wayfarer.Tests/TestResults/coverage/coverage.cobertura.xml`).
- **New coverage since prior refresh**:
  - **Controllers**: Admin/Manager/User/API/Public broadly covered; Manager search skip noted above.
  - **Services/Helpers**: ApplicationSettings, Registration, ApiToken, TileCache (store/purge + LRU eviction + LastAccessed updates), map thumbnail file maintenance (TripMapThumbnailGenerator non-Playwright paths), RazorViewRenderer, QuartzHostedService, CoordinateTimeZoneConverter basics, TimespanHelper, HtmlHelpers.
  - **Parsers/Jobs**: CSV/GPX/KML/GeoJSON/Timeline parsers, Trip exporters, location import job hooks, cleanup jobs, job listeners.
  - **Middleware**: DynamicRequestSizeMiddleware, PerformanceMonitoringMiddleware.
- **CRUD coverage**: Trip/User/Location CRUD across Admin/Manager/User/API; imports/exports, bulk edits/deletes; invitations/groups/tags; timeline/user/public surfaces (except the one skipped search test).
- **Low-covered targets surfaced by the latest report (quick-win first)**:
  - `Swagger/RemovePostGisSchemasDocumentFilter`: pure schema pruning; straightforward unit harness.
  - `Converters/PointJsonConverter`: round-trip Point JSON serialization/deserialization.
  - `Services/TileCacheService` helpers: `GetCacheFileSizeInMbAsync`, `GetTotalCachedFilesAsync`, `GetLruCachedInMbFilesAsync`, `GetLruTotalFilesInDbAsync`, plus purge/LRU helper branches (temp cache dir + EF InMemory/SQLite).
  - `Services/TripTagService`: popular/suggestions/orphan cleanup paths (requires PostgreSQL-specific SQL: `ILIKE`, `COUNT(*)::int`; needs Postgres/Testcontainers to exercise).
  - Deferred due to infra: `Services/LocationService` (PostGIS/raw SQL bbox sampling), `ApplicationDbContextSeed` (Postgres DDL), `Program` host wire-up, Playwright-heavy map snapshot flows.
- **Constraints**: Keep tests in `tests/Wayfarer.Tests`; no production edits for this phase; do not test 3rd-party middleware; always run `dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj` before committing.

---

## 2) Next Test Batch (Prioritized, Backend Only)

### A. Quick-win unit/service coverage (from latest report)
- **Swagger filter**: Cover `RemovePostGisSchemasDocumentFilter` pruning/removal of refs.
- **Point JSON**: Round-trip tests for `PointJsonConverter` (NTS Point to/from JSON).
- **Tile cache helpers**: Size/count helpers + purge/LRU branches using temp cache dir + EF InMemory/SQLite.
- **Trip tags**: Popular/suggestion/orphan cleanup paths – **requires PostgreSQL/Testcontainers** because service uses Postgres-only SQL (`ILIKE`, `::int`); defer until infra is available.
- **Controllers with low coverage (no special infra needed)**: User Area (`GroupsController` CRUD/invites/members/map, `HiddenAreasController`, `InvitationsController`, `LocationController` bulk note/hidden, `TimelineController` stats/navigation), core `TripExportController` (progress/exports), `HomeController`, `ErrorController`.

### B. Map Snapshot & Public Tiles (focused stubs)
- **Targets**: MapSnapshotService (stub Playwright/page to exercise install/screenshot fallback); Public TilesController referer/403/404 paths; TileCacheService concurrency/size accounting under stress.
- **Approach**: Fake Playwright/page objects; temp cache dirs + fake HttpClient; assert eviction ordering, LastAccessed movement, referer gating. No production code changes.

### C. Quartz/Infrastructure Guards
- **Targets**: QuartzSchemaInstaller (embedded SQL path), add negative/edge cases around scheduler wiring if feasible.
- **Approach**: Use in-memory/fake DbConnection to assert SQL load/execution decisions; avoid real Postgres.

### D. Public Viewers & Timelines
- **Targets**: Public TripViewer/UsersTimeline edge cases, remaining TilesController branches.
- **Approach**: WebApplicationFactory with seeded data; assert auth/visibility and file-result metadata; keep PostGIS off.

### E. Heavy Deps (defer but note)
- **Targets**: PostGIS/Testcontainers runs (LocationService + seeds), real Playwright/browser snapshot, external tile calls.
- **Approach**: Schedule after A-D; requires infra setup and explicit approval.

---

### Specialized Setup & Mocks
- **Heavy deps deferred**: PostGIS/Testcontainers, real Playwright/browsers, external map tile calls—only after A-D are green.
- **Mocking strategy**: Extend fakes for reverse geocoding, SSE, filesystem/tile IO, headless snapshots, scheduler abstractions; keep DB on EF InMemory; avoid 3rd-party middleware.

### Working Agreements
- Tests reside only in `tests/Wayfarer.Tests`; production code remains unchanged for this phase.
- Always run `dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj` before any commit to `feature/test-coverage-expansion`.
- Continue backend-first; JS/Leaflet/Quill tests are a later iteration once backend coverage stabilizes.
