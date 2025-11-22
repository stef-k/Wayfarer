# Wayfarer Test Coverage Status & Next Steps (Backend-First)

_Date: 2025-11-22 · Scope: Backend only (controllers/services/jobs); JS deferred_

This refresh compares the current test suite against the archived **test-coverage-expansion-plan.md** and summarizes what is covered, what remains, and the next high-ROI test batch. All work continues in `tests/Wayfarer.Tests` only; production code stays untouched. Every new test must run clean (`dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj`) before commit.

---

## 1) Current Coverage Status

- **Suite size**: 596 passing, 1 skipped (ManagerUsers search/ILike) across 70+ C# test files. Backend-only; JS/browser automation remains deferred.
- **New coverage since prior refresh**:
  - **Controllers**: Admin/Manager/User/API/Public broadly covered; Manager search skip noted above.
  - **Services/Helpers**: ApplicationSettings, Registration, ApiToken, TileCache (store/purge + LRU eviction + LastAccessed updates), map thumbnail file maintenance (TripMapThumbnailGenerator non-Playwright paths), RazorViewRenderer, QuartzHostedService, CoordinateTimeZoneConverter basics, TimespanHelper, HtmlHelpers.
  - **Parsers/Jobs**: CSV/GPX/KML/GeoJSON/Timeline parsers, Trip exporters, location import job hooks, cleanup jobs, job listeners.
  - **Middleware**: DynamicRequestSizeMiddleware, PerformanceMonitoringMiddleware.
- **CRUD coverage**: Trip/User/Location CRUD across Admin/Manager/User/API; imports/exports, bulk edits/deletes; invitations/groups/tags; timeline/user/public surfaces (except the one skipped search test).
- **Remaining gaps (highest impact first)**:
  - **MapSnapshotService**: Playwright browser install + request routing + data URI vs screenshot fallback (heavy deps).
  - **Tile cache deep paths**: concurrency/size accounting under load; Public TilesController referer enforcement/403/404; PostGIS/tile referer constraints.
  - **QuartzSchemaInstaller**: embedded SQL install path (DB connection/fake needed).
  - **Public viewers**: TripViewer/UsersTimeline edge cases; TilesController residual paths.
  - **Heavy infra**: PostGIS/Testcontainers, real Playwright/browser runs remain deferred per agreement.
- **Constraints**: Keep tests in `tests/Wayfarer.Tests`; no production edits for this phase; do not test 3rd-party middleware; always run `dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj` before committing.

---

## 2) Next Test Batch (Prioritized, Backend Only)

### A. Map Snapshot & Public Tiles (highest ROI, focused stubs)
- **Targets**: MapSnapshotService (stub Playwright/page to exercise install/screenshot fallback); Public TilesController referer/403/404 paths; TileCacheService concurrency/size accounting under stress.
- **Approach**: Fake Playwright/page objects; temp cache dirs + fake HttpClient; assert eviction ordering, LastAccessed movement, referer gating. No production code changes.

### B. Quartz/Infrastructure Guards
- **Targets**: QuartzSchemaInstaller (embedded SQL path), add negative/edge cases around scheduler wiring if feasible.
- **Approach**: Use in-memory/fake DbConnection to assert SQL load/execution decisions; avoid real Postgres.

### C. Public Viewers & Timelines
- **Targets**: Public TripViewer/UsersTimeline edge cases, remaining TilesController branches.
- **Approach**: WebApplicationFactory with seeded data; assert auth/visibility and file-result metadata; keep PostGIS off.

### D. Heavy Deps (defer but note)
- **Targets**: PostGIS/Testcontainers runs, real Playwright/browser snapshot, external tile calls.
- **Approach**: Schedule after A–C; requires infra setup and explicit approval.

---

### Specialized Setup & Mocks
- **Heavy deps deferred**: PostGIS/Testcontainers, real Playwright/browsers, external map tile calls—only after A–C are green.
- **Mocking strategy**: Extend fakes for reverse geocoding, SSE, filesystem/tile IO, headless snapshots, scheduler abstractions; keep DB on EF InMemory; avoid 3rd-party middleware.

### Working Agreements
- Tests reside only in `tests/Wayfarer.Tests`; production code remains unchanged for this phase.
- Always run `dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj` before any commit to `feature/test-coverage-expansion`.
- Continue backend-first; JS/Leaflet/Quill tests are a later iteration once backend coverage stabilizes.
