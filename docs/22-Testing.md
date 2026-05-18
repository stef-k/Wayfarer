# Testing

Approach
- Add xUnit tests under `tests/Wayfarer.Tests` (recommended structure).
- Focus on Services and Parsers for unit tests; add integration tests for critical flows (imports, trip exports, API auth).

Running Tests
- `dotnet test`
- Trip Editor E2E: `npm run test:e2e:trip-editor`

Trip Editor Asset-Mode Smoke
- These smokes are explicit opt-in checks. They do not run as part of `npm run test:e2e:trip-editor`.
- Development smoke proves ASP.NET Development + Vite dev-server integration only.
- Published smoke proves `dotnet publish` output and production bundle serving only.
- Neither smoke proves CRUD or editor workflow behavior. Those contracts are covered by the earlier #297 CRUD, error-state, search-add, and rich-notes batches.
- Configure the same `WAYFARER_E2E_USERNAME`, `WAYFARER_E2E_PASSWORD`, and `WAYFARER_E2E_TRIP_ID` values used by Trip Editor Playwright verification. The runner also reads ignored `.local/manual-verification.md`.
- Optional URLs:
  - `WAYFARER_ASSET_SMOKE_DEV_URL` defaults to `WAYFARER_E2E_BASE_URL` or `http://localhost:5012`.
  - `WAYFARER_ASSET_SMOKE_PUBLISHED_URL` defaults to `http://localhost:5013`.
- Published smoke runs `dotnet frontend build`, `npm run build`, and `dotnet publish Wayfarer.csproj -c Release -o .local/publish-smoke`, then starts the published app in non-Development mode. It uses `ConnectionStrings__DefaultConnection` when set, otherwise it falls back to the local `appsettings.Development.json` connection string while still running the app with `ASPNETCORE_ENVIRONMENT=Production`.
- Generated output and server logs stay under ignored `.local/...` paths.

```powershell
npm run smoke:trip-editor:assets:dev
npm run smoke:trip-editor:assets:published
npm run smoke:trip-editor:assets
```

Trip Editor Playwright Verification
- This is dev-only tooling for the Vue Trip Editor. It is not part of production deployment, and `npm run build` does not run Playwright.
- Start the ASP.NET Core app first:

```powershell
dotnet run --urls http://localhost:5012
```

- Start Vite in a second terminal:

```powershell
npm run dev
```

- Configure credentials in the test terminal with environment variables:

```powershell
$env:WAYFARER_E2E_BASE_URL='http://localhost:5012'
$env:WAYFARER_E2E_USERNAME='user'
$env:WAYFARER_E2E_PASSWORD='local-password'
$env:WAYFARER_E2E_TRIP_ID='15993426-552d-40e5-bd74-86dea34a3bf8'
npm run test:e2e:trip-editor
```

- As an alternative, put the same keys in ignored `.local/manual-verification.md` as simple `KEY=value` lines or PowerShell `$env:KEY='value'` lines. Environment variables win when both sources are present.
- Required keys are `WAYFARER_E2E_BASE_URL`, `WAYFARER_E2E_USERNAME`, `WAYFARER_E2E_PASSWORD`, and `WAYFARER_E2E_TRIP_ID`.
- Do not commit credentials, screenshots, traces, videos, browser profiles, or Playwright reports.
- Do not reset passwords or create users for E2E verification unless that action is explicitly approved.
- Install browser binaries locally when needed:

```powershell
npx playwright install
```

- If browser verification cannot run, report the exact reason, such as missing `WAYFARER_E2E_*` settings, ASP.NET not running, Vite not running, or missing Playwright browser binaries. Do not describe skipped browser checks as passed.

Trip Editor Test Credibility Matrix
- This matrix is the durable #297 claim-honesty artifact for Trip Editor tests.
- Labels:
  - `contract`: proves the final user-visible outcome for the covered behavior.
  - `backend-rule`: proves server mutation, authorization, validation, persistence, or read mapping.
  - `integration`: drives the UI against a real endpoint path and asserts a user-visible outcome.
  - `mocked-only`: uses fixture state or `page.route(...).fulfill(...)` for the relevant behavior; it proves frontend behavior only.
  - `proxy`: proves selector, button, request, or presence behavior without proving persistence.
  - `missing`: no meaningful automated proof exists yet.
  - `out of scope`: not part of Trip Editor E2E unless an editor entry point directly depends on it.
- A Playwright test that uses `page.route(...).fulfill(...)` for a mutation response is not E2E CRUD proof.
- Mocked mutation tests may prove frontend rendering, request shape, loading/error state, visual behavior, map adapter behavior, or response handling only.
- CRUD release proof requires backend-rule coverage plus real endpoint UI coverage, reload/API reread, or an explicit pairing that names both sides.

| Workflow | Current automated proof | Classification | Current claim boundary | Remaining #297 batch work |
|---|---|---:|---|---|
| Trip metadata save, public trip, share progress | Backend metadata/share-progress tests plus `tripEditorRemainingParity.spec.ts` real PATCH success. | backend-rule + proxy | Proves endpoint success and UI feedback, not reload/API reread persistence. | Add real reload/API reread if release proof needs persisted URLs/toggles. |
| Region create/edit/delete/reorder | Backend region mutation tests; browser coverage is partial. | backend-rule | Proves server rules. Browser create/delete/reorder outcome is not yet release proof. | Batch 2 real endpoint UI flow if region CRUD is release-critical. |
| Place create/edit/delete/reorder/move/Unassigned Places | Backend place mutation tests; `tripEditorPlaceReassignment.spec.ts`, `tripEditorIconSelector.spec.ts`, and marker feedback specs use mocked mutation responses. | backend-rule + mocked-only | Mocked tests prove request shape, affected-slice rendering, icon/feedback UI, and visual behavior only. | Batch 2 real endpoint place move/reorder/create coverage with reload/API reread. |
| Place geosearch add | Backend geocode proxy tests; `tripEditorMapSearch.spec.ts` mocks geocode and opens drafts. | backend-rule + proxy | Proves proxy/search UI states, target selector behavior, pending marker, and draft handoff; it does not prove saved search-add persistence. | Batch 4 real Save Place from search-add draft with reload/API reread. |
| Place coordinate pick | Backend coordinate mutation tests; `tripEditorPlaceCoordinateMapWork.spec.ts` uses fixture state and a mocked save response. | backend-rule + mocked-only | Proves map-work draft behavior, request body, and no geocode side effects only. | Batch 2 real coordinate save/reload if coordinate persistence is release-critical. |
| Area create/edit/delete/reorder/polygon map work | Backend area mutation tests; `tripEditorAreaEditing.spec.ts` mocks create/update/order responses. | backend-rule + mocked-only | Proves polygon draft behavior, request shape, interior-ring request preservation, and mocked order response handling. | Batch 2 real area create/save/reorder/delete coverage with reload/API reread. |
| Segment create/edit/delete/reorder/route map work | Backend segment mutation tests; `tripEditorSegmentEditing.spec.ts` mocks route-save and order responses. | backend-rule + mocked-only | Proves route draft behavior, request shape, and mocked order response handling. | Batch 2 real segment create/route-save/reorder/delete coverage with reload/API reread. |
| Visit progress/history | Backend read-state tests; Playwright visit specs use synthetic read models. | backend-rule + mocked-only | Proves mapper/read shape and component rendering, not a real UI fixture against seeded visit data. | Optional real read-only UI smoke if a release candidate finds a regression. |
| Rich notes text/images/alignment/sanitize/save | Rich notes Playwright specs use mocked editor mutation routes; backend notes coverage is partial through owner mutation tests. | mocked-only + partial backend-rule | Proves client canonicalization, sanitizer request shape, proxy image rendering, and editor UX only. | Batch 5 backend sanitizer/persistence plus one real UI save/reload. |
| Sidebar search/filter | `tripEditorSidebarSearch.spec.ts` and remaining parity coverage drive the real app without backend search requests. | contract | Proves frontend filter behavior and no search-provider calls. | No Batch 2 CRUD work. Keep frontend-only claim. |
| Map utilities: fit, recenter, focus, zoom display, measure, copy link | Utility smoke uses real UI; geometry variant tests may use synthetic read models. | contract + mocked-only | Utility behavior is credible frontend contract coverage; synthetic geometry variants prove map adapter behavior only. | Add no backend work unless a real geometry fixture gap appears. |
| Published production bundle loading | Dev-served editor smoke exists; published-output Production smoke is absent. | missing | Current Playwright runs prove development ASP.NET + Vite mounting only. | Batch 6 published-output Production bundle smoke. |
| Light/dark/narrow layout | Layout and visual polish specs mix real app and fixture-backed states. | contract + mocked-only | Proves browser-visible layout/visual regressions; mocked data-dependent visuals are not CRUD proof. | No broad rewrite; keep labels honest. |
| Auth/ownership/error behavior | Backend auth/missing/cross-trip coverage exists; browser-visible error and stale mutation UX is partial. | backend-rule + missing | Server rules are covered more strongly than UI stale/failure feedback. | Batch 3 failed-save, stale entity, delete-failure feedback coverage. |
| Dirty/cancel/delete/error-feedback flows | Shared discard/delete confirmations and some feedback paths are covered, often fixture-backed. | contract + mocked-only + missing | Proves confirmation and frontend feedback states, not real delete persistence. | Batch 3 and Batch 2 real delete visual-removal pairing where needed. |
| Save & Exit and route/navigation-away behavior | Metadata navigation and dirty guards have partial UI coverage. | proxy + mocked-only | Proves navigation/feedback behavior only where named; not broad CRUD persistence. | Clarify scope before adding real coverage. |
| Development Vite server smoke | Standard Trip Editor Playwright run mounts the dev-served shell. | contract | Proves local ASP.NET + Vite dev integration only. | Keep separate from production bundle claims. |
| Import/export/backfill/public viewer/mobile/API | Not Trip Editor E2E unless the editor entry point depends on the behavior. | out of scope | Do not report these as Trip Editor release proof. | Track in separate issue/suite if needed. |

Coverage
- Install tools and generate HTML: `dotnet tool restore` then `.\tools\coverage-report.ps1`
- Reports land in `coverage-report/index.html` (cobertura XML in `tests/Wayfarer.Tests/TestResults/coverage/coverage.cobertura.xml`).
- Uses Coverlet (msbuild) + ReportGenerator; backend-only scope, no prod code changes.
- Compiled Razor views (`AspNetCoreGeneratedDocument*`) are excluded from coverage to keep numbers focused on backend code.

Targets
- Parsers: sample fixtures for GPX/KML/CSV/GeoJSON/Google JSON.
- Services: `LocationImportService`, `TripExportService`, `ReverseGeocodingService` (mock external calls).
- API: controller tests using `WebApplicationFactory` and in-memory DB or test containers.

Guidelines
- Keep tests focused and deterministic.
- Avoid real secrets and external network calls in tests.

