# Testing

Approach
- Add xUnit tests under `tests/Wayfarer.Tests` (recommended structure).
- Focus on Services and Parsers for unit tests; add integration tests for critical flows (imports, trip exports, API auth).

Proportionate Validation Policy
- Product correctness is the objective; producing an exhaustive browser harness is not.
- Select the lowest stable seam that can prove the behavior:
  - pure/unit tests for algorithms, validation, parsing, limits, and state machines;
  - client/component tests for reactive transitions, cancellation, stale completion, and per-entity isolation;
  - focused PostgreSQL tests for migrations, constraints, transactions, locking, persistence, and recovery;
  - Playwright for a small number of browser-only facts such as mounted rendering, native interaction, focus, layout, and one representative cross-layer journey.
- The normal browser cap for one issue is one critical happy path plus at most one risk-specific negative observation. Role, theme, viewport, lifecycle, provider, and failure variants belong below the browser layer unless the variant is itself visual or interaction-specific.
- Do not require a single browser test to prove every lifecycle transition. Long serial workflows are fragile: one fixture precondition prevents all later evidence and turns ordinary setup mistakes into false release blockers.
- Do not duplicate proof. A deterministic production-store test plus one mounted visibility smoke is stronger and cheaper than replaying the entire store matrix through Playwright. A PostgreSQL transaction test plus one real Save smoke is sufficient without repeating every relational failure in the browser.
- A fixture/locator/port/host/timing failure is classified as harness evidence. It becomes a product finding only after a production counterexample is established.
- After the first harness-only failure, permit one diagnosis/correction and one full rerun. If the same selection fails again for harness reasons, stop rebuilding the environment, report the exact unavailable evidence, and proceed or request a decision only if the remaining product risk is material.
- Never impose an agent-created promise such as “one more run only” that prevents correcting a trivial fixture mistake within the allowed correction cycle. Conversely, do not spend repeated sessions chasing complete browser coverage after the cap is reached.
- When a prerequisite repeatedly has to be rediscovered, update this runbook or create a dedicated reusable test-infrastructure issue. Product issues must not each invent a temporary database/account/host orchestration framework.

Trip Editor Browser Preflight
- Decide the evidence class before starting:
  1. Use client/component tests when the claim is state transitions, races, cancellation, or reactivity.
  2. Use focused PostgreSQL tests when the claim is persistence, concurrency, measurements, or cleanup.
  3. Use the configured reusable Trip Editor fixture for mounted UI smoke.
  4. Create an isolated end-to-end database only when one inseparable cross-layer journey is the actual risk.
- Check all browser prerequisites once, before creating fixtures or starting a long run:
  - Playwright Chromium launches;
  - the selected ASP.NET and Vite ports are free or the intended hosts are healthy;
  - the database connection is reachable;
  - `WAYFARER_E2E_BASE_URL`, `WAYFARER_E2E_USERNAME`, `WAYFARER_E2E_PASSWORD`, and `WAYFARER_E2E_TRIP_ID` resolve from the environment or `.local/manual-verification.md`;
  - the configured Trip endpoint returns success and contains the minimum entities required by the selected smoke.
- If those prerequisites are absent, do not start the workflow and discover the same failure after provisioning. Report it immediately as unavailable evidence.
- Use the existing `playwright.config.ts`, `tripEditorConfig.ts`, `.local/manual-verification.md`, and ignored `.local/playwright` output locations. Do not create a parallel runner merely to avoid these contracts.
- A product-specific fake upstream is appropriate when the upstream protocol is under test. It must not replace Wayfarer generation, acceptance, mutation, or persistence endpoints in the one real cross-layer smoke.
- Cleanup only run-owned processes, ports, database rows/databases, profiles, and artifacts. Preserve user-owned hosts and PostgreSQL instances.

Running Tests
- `dotnet test`
- Trip Editor E2E: `npm run test:e2e:trip-editor`

Pull Request Merge Gate
- The GitHub Actions `test` check for the current PR head is authoritative merge evidence.
- Inspect the actual PR check with `gh pr checks <pr-number>` and wait until it reports success before invoking `gh pr merge`.
- Do not infer safety from the merge button, `gh pr checks --required`, or `gh pr merge --auto`; repository settings can allow an administrator to merge while a non-required check is still pending.
- Pending, missing, cancelled, neutral, or failed executions are not a passing gate.
- If a run clearly stalls in runner/package setup before reaching repository code, cancel it and rerun the unchanged workflow once. If that rerun also fails or stalls, report CI infrastructure failure instead of modifying product code or repeatedly rebuilding the environment.
- Documentation-only changes under `docs/` or in Markdown files take the workflow's fast path: the `test` job succeeds without running restore, build, ordinary tests, or Playwright. Workflow, configuration, source, test, migration, and dependency changes always run the complete job.

.NET Playwright Rendering Test
- The .NET rendering test owns a browser cache separate from JavaScript Playwright.
- Restore and build first so Microsoft.Playwright generates its version-coupled installer.
- The installer and test process must receive the same absolute `PLAYWRIGHT_BROWSERS_PATH`.
- CI derives the .NET Chromium cache identity from the generated Release `browsers.json`; package versions and browser revisions are not copied into the key manually.

```powershell
dotnet restore
$env:MvcFrontendKitEnabled = 'false'
dotnet build tests/Wayfarer.Tests/Wayfarer.Tests.csproj --configuration Release --no-restore

$dotnetBrowserCache = [IO.Path]::GetFullPath('.local/playwright/dotnet-browsers')
$artifactDirectory = [IO.Path]::GetFullPath('.local/test-results/415')
New-Item -ItemType Directory -Force $dotnetBrowserCache, $artifactDirectory | Out-Null
$env:PLAYWRIGHT_BROWSERS_PATH = $dotnetBrowserCache
$env:WAYFARER_TEST_ARTIFACT_DIRECTORY = $artifactDirectory

pwsh tests/Wayfarer.Tests/bin/Release/net10.0/playwright.ps1 install chromium

dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj `
    --configuration Release `
    --no-build `
    --filter "Category=RequiresPlaywright"

# Optional: run every discovered .NET test with the same browser cache.
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj `
    --configuration Release `
    --no-build
```

- Retained PDF and screenshot evidence is written only when `WAYFARER_TEST_ARTIFACT_DIRECTORY` is set.
- Cleanup is limited to the issue-owned directories created above:

```powershell
Remove-Item -LiteralPath $dotnetBrowserCache -Recurse -Force
Remove-Item -LiteralPath $artifactDirectory -Recurse -Force
```
- Do not delete global Playwright caches, production `ChromeCache`, browser profiles, JavaScript browser assets, or unrelated `.local` content.

Trip Editor Asset-Mode Smoke
- These smokes are explicit opt-in checks. They do not run as part of `npm run test:e2e:trip-editor`.
- Built smoke validates preceding `npm run build` output deterministically and requires no tool restore, credentials, browser, host, Trip, or database.
- Development smoke proves ASP.NET Development + Vite dev-server integration only; it does not run the CLI build or restore .NET tools.
- Published smoke proves `dotnet publish` output and production bundle serving only.
- Neither smoke proves CRUD or editor workflow behavior. Those contracts are covered by the earlier #297 CRUD, error-state, search-add, and rich-notes batches.
- Configure the same `WAYFARER_E2E_USERNAME`, `WAYFARER_E2E_PASSWORD`, and `WAYFARER_E2E_TRIP_ID` values used by Trip Editor Playwright verification. The runner also reads ignored `.local/manual-verification.md`.
- Optional URLs:
  - `WAYFARER_ASSET_SMOKE_DEV_URL` defaults to `WAYFARER_E2E_BASE_URL` or `http://localhost:5012`.
  - `WAYFARER_ASSET_SMOKE_PUBLISHED_URL` is optional. When unset, the runner allocates a free `127.0.0.1` port for the published app. When set, that URL/port must be free before launch so the smoke cannot pass against an older server.
- Published and all modes restore repository-local .NET tools before running `dotnet frontend build`, `npm run build`, and `dotnet publish Wayfarer.csproj -c Release -o .local/publish-smoke`, then start the published app in non-Development mode. They require usable Trip Editor credentials/config, a reachable configured database, and either `ConnectionStrings__DefaultConnection` or a local `appsettings.Development.json` connection string while still running the app with `ASPNETCORE_ENVIRONMENT=Production`.
- Generated output, cache folders, and server logs stay under `.local/publish-smoke`, `.local/asset-smoke`, and `.local/asset-smoke-cache`, which are ignored by committed `.gitignore` rules.

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
- Install JavaScript browser binaries into their own run-owned cache and use that same absolute path for execution:

```powershell
$jsBrowserCache = [IO.Path]::GetFullPath('.local/playwright/js-browsers')
New-Item -ItemType Directory -Force $jsBrowserCache | Out-Null
$env:PLAYWRIGHT_BROWSERS_PATH = $jsBrowserCache
npx playwright install chromium
npx playwright test --config=playwright.config.ts
```

- Do not use the generated .NET installer or .NET browser cache for JavaScript tests, and do not delete global Playwright caches during local cleanup.

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
- The test project uses xUnit v2 with the xUnit Visual Studio adapter and the default VSTest execution model. It does not opt into Microsoft Testing Platform.
- `coverlet.collector` is the sole coverage integration. The repository does not use Coverlet's MSBuild properties or console tool.
- Restore repository-local tools and generate HTML with `.\tools\coverage-report.ps1`. The script builds the Debug test project, runs `dotnet test --collect:"XPlat Code Coverage"` with `coverlet.runsettings`, and fails unless the current run produces one non-empty Cobertura file and a non-empty `coverage-report/index.html`. HTML output is constrained to `coverage-report` or one of its descendants; an existing output must carry the script's ownership marker, so unrelated directories are never recursively replaced.
- Ordinary coverage uses `Category!=RequiresSpatialite&Category!=RequiresPlaywright`. PostgreSQL tests remain discoverable but skip unless `WAYFARER_TEST_POSTGRES_CONNECTION` identifies the dedicated test database; the coverage command does not install browsers, load SpatiaLite, or contact an unconfigured PostgreSQL fixture.
- Current-run Cobertura XML is isolated temporarily below `tests/Wayfarer.Tests/TestResults/coverage-report/<run-id>/`; the script never searches older result directories and removes only that invocation's exact run directory after success or failure.
- Compiled Razor views (`AspNetCoreGeneratedDocument*`) are excluded from coverage to keep numbers focused on backend code.
- List opt-in cases without executing their infrastructure with `dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj --list-tests --filter "Category=RequiresPlaywright"`, `--filter "Category=RequiresSpatialite"`, or `--filter "FullyQualifiedName~Postgres"`. Listing is discovery evidence only; execute an opt-in selection only after provisioning its documented prerequisite.

Targets
- Parsers: sample fixtures for GPX/KML/CSV/GeoJSON/Google JSON.
- Services: `LocationImportService`, `TripExportService`, `ReverseGeocodingService` (mock external calls).
- API: controller tests using `WebApplicationFactory` and in-memory DB or test containers.

Guidelines
- Keep tests focused and deterministic.
- Avoid real secrets and external network calls in tests.

# Segment measurement provider tests

Issue 405 provider tests require `WAYFARER_TEST_POSTGRES_CONNECTION` to identify the dedicated `wayfarer_import_tests` PostgreSQL 17/PostGIS database. They execute the exact-base provenance migration, backfill, enum constraint, downgrade/re-upgrade, row-lock ordering, rollback/tracker recovery, and referenced profile-speed reconciliation. A skipped provider test is unavailable evidence and must not be reported as a pass.
