# Testing

## Code Guard

Install the published distribution with `pipx install agent-code-guard==0.3.1` (or an isolated Python virtual environment), then run `code-guard --version` and `code-guard doctor --json`. Doctor must report healthy installation, configuration, Git, skill, and parser providers.

Normal agent work uses `code-guard . --changed-only --json --json-mode compact`. Before completion, inspect the complete branch with `code-guard . --base-ref main --ci`. Outside Git, pass exact edited paths. `code-guard . --ci` is a deliberate full audit. Code Guard complements compilers, tests, linters, security checks, and design review.

PASS exits 0. REVIEW exits 1 normally and 0 with `--ci`; inspect each changed-code finding and justify it or improve cohesion. FAIL exits 2; INCOMPLETE and tool/configuration errors exit 3 and block completion. Doctor has its own status: healthy exits 0, unhealthy exits 1, invocation/internal errors exit 3. Do not suppress unsuccessful exits. Load only bundled guidance identified by `requiredPolicies`.

The six guards remain enabled at shipped thresholds. LOC above 400 triggers review and above 600 fails for new files; exactly 400 passes and exactly 600 reviews. The review-level ratchet in `.agent-tools/code-guard.loc-baseline.json` was generated from current source during adoption. It records exact nonblank LOC allowances only for files above 400, preserving legacy sizes while rejecting growth. Unlike the retired historical allowance, reductions already made before adoption are retained. New files remain subject to the hard cap.

Normal analysis never changes the ratchet. `code-guard . --create-loc-baseline` is a one-time adoption operation; do not recreate the file to accept growth. Use `code-guard . --update-loc-baseline` only to lower or prune allowances after genuine reductions. Review the resulting diff; increases require an explicit policy decision.

`.agent-tools/code-guard.config.json` excludes generated migrations, `*.Designer.cs`, `*.g.cs`, `*.generated.cs`, minified JS/CSS, vendored `wwwroot/lib`, generated `wwwroot/dist` and `wwwroot/vite`, and the retained legacy Image/Tile caches and Uploads. These are all-guard boundaries; project-owned source/tests receive every supported guard. Built-in exclusions also omit build outputs, dependency directories, and local scratch artifacts.

CI installs exactly 0.3.1 in an isolated Python 3.12 environment, verifies the version and doctor, and checks the event's exact PR base SHA with `--base-ref "$BASE_SHA" --ci`. Checkout retains full history. This gate runs even for documentation-only PRs; REVIEW remains visible, while FAIL/INCOMPLETE/tool errors fail the required `test` job.

Find the version-matched agent skill with `code-guard --skill-path`. Activate that path where supported, or use `code-guard --export-skill <empty-target>` for the platform's global skill directory. Never overwrite a non-empty export; verify its `.agent-code-guard-version` marker matches 0.3.1. Global skill files are separate from repository commits. No hooks or compatibility runners are required.

## Testing approach

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

Reusable Environment Discovery
- A missing environment variable means the prerequisite is not attached to the current process; it does not prove that the underlying service or runtime is absent.
- Before reporting PostgreSQL evidence as unavailable, inspect the Linux PostgreSQL service (`pg_isready`, `pg_lsclusters`), installed `psql`/`createdb` tools, the persistent `wayfarer_import_tests` database, the shell-exported `WAYFARER_TEST_POSTGRES_CONNECTION` (or Windows service/tools and user-scoped variable on Windows), and existing repository runners such as `tools/run-407-waypoint-browser.ps1`.
- Before reporting browser evidence as unavailable, distinguish the Codex in-app browser backend from repository Playwright. The absence of an attached in-app browser does not prevent `npx playwright` or the generated .NET `playwright.ps1` from launching Chromium.
- Inspect existing Playwright caches and installers before reinstalling. Install the required version into the documented cache when it is missing, then run one launch/readiness check before the selected tests.
- An agent may declare infrastructure unavailable only after these discovery and repair steps fail or require credentials/authority that are genuinely absent. Report the exact failed prerequisite and command; do not substitute “environment variable missing” for environment discovery.

## Persistent PostgreSQL Test Database
- Local relational work uses PostgreSQL 17 as the maintainer/guarded-test qualification baseline and the dedicated database named exactly `wayfarer_import_tests`. This test baseline does not raise the documented PostgreSQL 13+ general self-hosting/runtime minimum. Never point guarded tests at the normal `wayfarer` development database or a production database.
- `WAYFARER_TEST_POSTGRES_CONNECTION` is the safety attachment consumed by the test process. On Linux/WSL, export it from private shell configuration into the test process; keep credentials out of Git and command output. On Windows, store it at user scope and copy it into the current process before running tests.
- The dedicated database is reusable. Tests must isolate their own schemas or rows and clean only their owned data. Do not recreate the database for every issue merely because the process environment is empty.
- If the database is not present, use the installed PostgreSQL 17 tools and the existing local administrator connection to create only `wayfarer_import_tests`, then install PostGIS when the selected fixture requires it. Do not print or commit the password.

```bash
# Attach the dedicated connection without echoing it or adding it to shell history.
read -rsp 'Dedicated wayfarer_import_tests connection: ' WAYFARER_TEST_POSTGRES_CONNECTION
printf '\n'
export WAYFARER_TEST_POSTGRES_CONNECTION
# Ordinary suite: includes opt-in PostgreSQL tests; excludes browser/SpatiaLite lanes.
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj --filter 'Category!=RequiresSpatialite&Category!=RequiresPlaywright'
```

The connection must name `Database=wayfarer_import_tests`. A missing attachment causes opt-in tests to skip; skipped tests are not PostgreSQL qualification. For future shells, an untracked private shell configuration may export the same connection. Do not substitute the application connection string.

Windows attachment:

```powershell
# Read the already-configured user-scoped connection without displaying it.
$testConnection = [Environment]::GetEnvironmentVariable(
    'WAYFARER_TEST_POSTGRES_CONNECTION',
    'User')
if ([string]::IsNullOrWhiteSpace($testConnection)) {
    throw 'Configure the persistent wayfarer_import_tests connection at Windows user scope.'
}
$env:WAYFARER_TEST_POSTGRES_CONNECTION = $testConnection
```

- The guarded fixture parses the connection with Npgsql and rejects every database name other than exactly `wayfarer_import_tests`; let that fixture remain the final safety authority instead of echoing or reparsing credentials in shell output.
- Repository runs that need complete isolation may still use the disposable cluster pattern in `tools/run-407-waypoint-browser.ps1`, but that is an exceptional cross-layer fixture, not the default answer to a missing process variable.

Disposable Migration-History Database
- Ordinary row-level and concurrency PostgreSQL tests continue to use the persistent `wayfarer_import_tests` fixture. Tests grouped in `PostgreSQL migration history` use one empty disposable database per collection fixture because they migrate the real schema down and up.
- The disposable owner derives its server and credentials from `WAYFARER_TEST_POSTGRES_CONNECTION`. The source must connect to PostgreSQL 17 and name exactly `wayfarer_import_tests`; the role must be allowed to create databases and enable the `postgis` and `citext` extensions required by the real EF migration history.
- Every disposable name is generated internally as `wayfarer_migration_tests_<32-hex-guid>`. Create, session termination, and drop operations validate that exact prefix and suffix and reject `wayfarer_import_tests`, `wayfarer`, `postgres`, `template0`, and `template1`. Identifiers are quoted with Npgsql, while session termination uses a database-name parameter.
- The fixture instance retains the only cleanup authority. It closes fixture contexts, clears its connection pool, terminates sessions only for its exact retained name, and drops only that database. Initialization cleanup preserves the primary failure and retains bounded cleanup diagnostics.
- A retained disposable database indicates interrupted or failed cleanup. Diagnose it with a read-only catalog query for names beginning with the full `wayfarer_migration_tests_` prefix and inspect active sessions for that exact name. Do not use wildcard deletion, enumerate-and-drop scripts, or recreate `wayfarer_import_tests`; remove a retained database only after confirming its complete guarded name and ownership.

## Trip Editor Browser Preflight
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
- If those prerequisites are absent, diagnose them before starting the workflow: attach the persistent database/credentials, start or verify the intended hosts, and install the correct Chromium runtime using the commands below. Report unavailable evidence only when that repair requires missing authority or fails once for a concrete infrastructure reason.
- Use the existing `playwright.config.ts`, `tripEditorConfig.ts`, `.local/manual-verification.md`, and ignored `.local/playwright` output locations. Do not create a parallel runner merely to avoid these contracts.
- A product-specific fake upstream is appropriate when the upstream protocol is under test. It must not replace Wayfarer generation, acceptance, mutation, or persistence endpoints in the one real cross-layer smoke.
- Cleanup only run-owned processes, ports, database rows/databases, profiles, and artifacts. Preserve user-owned hosts and PostgreSQL instances.

Running Tests
- `dotnet test`
- Trip Editor typecheck: `npm run typecheck`
- Trip Editor E2E: `npm run test:e2e:trip-editor`

Trip Editor Typecheck
- Run `npm run typecheck` before client tests and `npm run build`; it checks both ordinary TypeScript modules and Vue single-file components included by `tsconfig.json`.
- [Issue #464](https://github.com/stef-k/Wayfarer/issues/464) adopts stable TypeScript 6, through the official `@typescript/typescript6` compatibility package, as the supported compiler for Vue SFC typechecking with `vue-tsc` 3.3.10.
- TypeScript 7 adoption is deferred to [issue #474](https://github.com/stef-k/Wayfarer/issues/474) because TypeScript 7.0 lacks the programmatic API required by stable Vue SFC tooling.
- The conditional frontend CI path runs this command after dependency audit and before client tests and the production build.

Pull Request Merge Gate
- The GitHub Actions `test` check for the current PR head is necessary merge evidence, not sufficient merge authorization.
- Inspect the actual PR check with `gh pr checks <pr-number>` and wait until it reports success. Do not infer safety from the merge button, `gh pr checks --required`, or `gh pr merge --auto`; repository settings can allow an administrator to merge while a non-required check is still pending.
- After implementation and validation, the implementation agent reports the exact PR head SHA and stops with the PR unmerged and the issue open. It does not merge its own PR or close the implementation issue.
- The exact head then receives an independent review separate from the implementation pass. In the maintainer workflow, Codex implements and ChatGPT reviews the live GitHub exact head. Implementation-time self-review, subagent review, or an unrecorded internal review does not satisfy this gate.
- Any commit after independent review invalidates that review. Run proportionate validation and exact-head CI again, then independently review the new head.
- Merge and issue closure require successful exact-head CI, independent review with no blocking findings, and maintainer acceptance.
- Pending, missing, cancelled, neutral, or failed executions are not a passing gate.
- If a run clearly stalls in runner/package setup before reaching repository code, cancel it and rerun the unchanged workflow once. If that rerun also fails or stalls, report CI infrastructure failure instead of modifying product code or repeatedly rebuilding the environment.
- Documentation-only changes under `docs/` or in Markdown files take the workflow's fast path: the `test` job succeeds without running restore, build, ordinary tests, or Playwright. The independent-review gate still applies. Workflow, configuration, source, test, migration, and dependency changes always run the complete job.

.NET Playwright Rendering Test
- The .NET rendering test owns a browser cache separate from JavaScript Playwright.
- Restore and build first so Microsoft.Playwright generates its version-coupled installer.
- The installer and test process must receive the same absolute `PLAYWRIGHT_BROWSERS_PATH`.
- CI derives the .NET Chromium cache identity from the generated Release `browsers.json`; package versions and browser revisions are not copied into the key manually.
- Local readiness order is: restore/build, locate the generated `playwright.ps1`, set one absolute cache path, install Chromium with that generated script, then execute the selected tests with the same path. A missing Codex browser backend is irrelevant to this CLI workflow.

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
- Keep the shared browser cache for subsequent runs. Explicit evidence under `.local/test-results` stays until its owner deliberately removes that exact evidence directory.
- Do not delete global Playwright caches, browser profiles, JavaScript browser assets, or unrelated `.local` content.

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
- The focused `embeddedMap.spec.ts` also uses this runner. Start the established local HTTPS host on port 7150, set `WAYFARER_E2E_BASE_URL=https://wayfarer.example.test:7150`, and run `npx playwright test --config=playwright.config.ts embeddedMap.spec.ts`. The reserved hostname is mapped to loopback only inside the test browser. The test discovers an existing public Trip without changing it and mounts production-generated HTML in a script-free cross-origin scrollable parent. Its local-network permission only allows that fixture to reach the local host; it is not an iframe attribute or production requirement. Chromium native input/CDP proves desktop and emulated mobile scroll chaining, controls, pan/zoom, and the full-view escape; it does not establish physical-device acceptance.
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

- Before installing, inspect `$env:LOCALAPPDATA\ms-playwright` and the selected `.local/playwright/js-browsers` cache. Reuse only a revision compatible with the current JavaScript Playwright package; otherwise run `npx playwright install chromium` into the selected cache.
- Verify the runtime itself before blaming application fixtures: `npx playwright install --dry-run chromium` identifies the expected revision and a minimal Playwright launch can prove the executable starts. Browser launch success, authenticated host readiness, and product behavior are separate evidence boundaries.

- Do not use the generated .NET installer or .NET browser cache for JavaScript tests, and do not delete global Playwright caches during local cleanup.

- If browser verification still cannot run after discovery and one repair attempt, report the exact reason, such as unavailable credentials, an unhealthy ASP.NET/Vite host, a failed version-coupled Chromium installation, or a reproducible launch error. Do not describe skipped browser checks as passed, and do not call Chromium unavailable merely because the in-app browser backend is absent.

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

### Test artifact ownership and disk lifecycle

- Ordinary `dotnet test` uses console results (no persistent logger/collector output by default). Filesystem fixtures remove their own exact roots through disposal/finally; the shared import staging authority finalizes its process-owned root on normal testhost exit, including test failures. Explicit logger/collector evidence should use `.local/test-results` or another non-allowlisted output path; coverage's temporary results have their own closing cleanup.
- Coverage retains one current successful HTML report by default. Current-run intermediate results and failed partial HTML are finalized automatically; unknown report entries remain untouched.
- Shared-layout E2E stops its exact recorded host before deleting its launcher-owned OS-temp source/build tree. Launcher exit and global teardown use the same path guards. Cleanup problems are reported separately from test/host failures.
- Non-built asset smoke replaces its exact `.local/asset-smoke` logs and `.local/asset-smoke-cache` at setup. After owned processes stop, it removes run-owned cache/publish output; successful runs also remove logs. Failure logs remain for diagnosis until the next smoke run.
- Playwright replaces its configured output/report directories on the next invocation; traces/videos/screenshots are retained only on failure. Explicit PDF/screenshot evidence is opt-in via `WAYFARER_TEST_ARTIFACT_DIRECTORY` and is never removed by maintenance.
- The #407 browser runner removes its exact run root on success and preserves the current failed run. It references `PLAYWRIGHT_BROWSERS_PATH` (default `.local/playwright/js-browsers`) instead of copying browser binaries into each retained run. Its disposable PostgreSQL cluster and published app can still make retained failures large.
- Shared JavaScript and .NET browser binaries, `node_modules`, npm/NuGet caches, and ordinary `bin/obj` are reusable dependencies, not ephemeral output. Browser services consume preinstalled version-matched bundles; service construction does not change browser discovery.

Use the cross-platform maintenance command only for stale/interrupted residue; it is **not** a closing step required after normal tests:

```sh
npm run test:cleanup -- --dry-run
npm run test:cleanup
npm run test:cleanup:safety
pwsh -NoProfile -File tools/coverage-report.safety.tests.ps1
pwsh -NoProfile -File tools/shared-layout.safety.tests.ps1
```

The command reports approximate bytes and accepts no deletion path or age override. It removes only these fixed ephemeral repository outputs: `tests/Wayfarer.Tests/TestResults`, `playwright-report`, `.local/playwright/{test-output,shared-layout-output,shared-layout-report}`, and `.local/{publish-smoke,asset-smoke,asset-smoke-cache}`. Run it when those repository-local producers are idle.

Exact direct `.local/407-waypoint-<32-lowercase-hex-guid>` children are reported separately as retained evidence and removed only after 24 hours without writes anywhere in the tree. Known OS-temp fixture prefixes (import, fixture, browser, image-cache, version, rendering, coverage safety and shared-layout), plus GUID children of `wayfarer-tile-tests` and `wayfarer-trip-editor-place-tests`, use the same age guard. Recent entries survive, as do shared-layout roots whose launcher PID is still alive. Unrecognized names, linked ancestors, symlinks/junctions/reparse points, and trees containing links are skipped without traversal.

Maintenance preserves `.local/test-results`, `.local/manual-verification.md`, shared browser caches, all unrelated `.local` content, inactive legacy residue, package caches, `bin/obj`, PostgreSQL databases outside exact stale #407 fixture roots, and uploads/runtime data. It never sweeps the whole repository, `.local`, or OS temp directory. Do not use broad recursive deletion as a replacement.

Coverage
- The test project uses xUnit v2 with the xUnit Visual Studio adapter and the default VSTest execution model. It does not opt into Microsoft Testing Platform.
- `coverlet.collector` is the sole coverage integration. The repository does not use Coverlet's MSBuild properties or console tool.
- Restore repository-local tools and generate HTML with `.\tools\coverage-report.ps1`. The script builds the Debug test project, runs `dotnet test --collect:"XPlat Code Coverage"` with `coverlet.runsettings`, and fails unless the current run produces one non-empty Cobertura file and a non-empty `coverage-report/<run-id>/index.html`.
- Each invocation creates a fresh internally generated GUID child under `coverage-report` and prints its exact path. After validating a new successful HTML report, older ordinary lowercase GUID report children are pruned. Unknown entries and links are preserved. A failed invocation removes only its partial report and retains the previous successful report. The script never recursively replaces a caller-selected directory.
- Ordinary coverage uses `Category!=RequiresSpatialite&Category!=RequiresPlaywright`. PostgreSQL tests remain discoverable but skip unless `WAYFARER_TEST_POSTGRES_CONNECTION` identifies the dedicated test database; the coverage command does not install browsers, load SpatiaLite, or contact an unconfigured PostgreSQL fixture.
- Current-run Cobertura XML is isolated temporarily below `tests/Wayfarer.Tests/TestResults/coverage-report/<run-id>/`; the script never searches older result directories and removes only that invocation's exact run directory after success or failure.
- Compiled Razor views (`AspNetCoreGeneratedDocument*`) are excluded from coverage to keep numbers focused on backend code.
- List opt-in cases without executing their infrastructure with `dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj --list-tests --filter "Category=RequiresPlaywright"`, `--filter "Category=RequiresSpatialite"`, or `--filter "FullyQualifiedName~Postgres"`. Listing is discovery evidence only; execute an opt-in selection only after provisioning its documented prerequisite.

Targets
- Parsers: sample fixtures for GPX/KML/CSV/Wayfarer GeoJSON/Google JSON.
- Services: `LocationImportService`, `TripExportService`, `ReverseGeocodingService` (mock external calls).
- Resumable enrichment uses fake HTTP for outcomes, guarded PostgreSQL for admission/attempt fairness and ownership, and the real Quartz ADO store for restart/misfire/reconciliation. Never contact public providers; give concurrency tests explicit timeouts and avoid sleep-based scheduler assertions.
- The persistent enrichment restart proof scopes every reconciliation and job context to its fixture-owned user, including workflow and attempt recovery; an isolated Quartz schema alone is insufficient. Its unrelated synthetic expired workflow/operation must remain unchanged through reconciliation and owner cleanup. Shut down existing schedulers before obtaining the same-name cleanup scheduler, run cleanup actions independently while preserving the primary failure, and verify owned domain rows, Quartz rows, scheduler registration, and schema removal. Never rerun an uncontained reconciler against shared domain state to reproduce retained evidence.
- API: controller tests using `WebApplicationFactory` and in-memory DB or test containers.

Guidelines
- Keep tests focused and deterministic.
- Ordinary correctness tests may use bounded timeouts as deadlock guards, but must not assert hosted-runner wall-clock scheduling or latency as product correctness. Put performance measurements in a separately controlled benchmark or performance workflow.
- Avoid real secrets and external network calls in tests.

# Segment measurement provider tests

Issue 405 provider tests require `WAYFARER_TEST_POSTGRES_CONNECTION` to identify the dedicated `wayfarer_import_tests` PostgreSQL 17/PostGIS database. They execute the exact-base provenance migration, backfill, enum constraint, downgrade/re-upgrade, row-lock ordering, rollback/tracker recovery, and referenced profile-speed reconciliation. A skipped provider test is unavailable evidence and must not be reported as a pass.
## ImageCache storage qualification

Run `dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj --filter 'FullyQualifiedName~ImageCache|FullyQualifiedName~ImageProxy|FullyQualifiedName~StoragePathsTests|FullyQualifiedName~DeploymentScriptTests'` first. Attach the guarded PostgreSQL connection above: `ImageCacheStoragePostgresTests` uses an owned disposable migrated database to prove restored absolute rows, promotion commit points, rollback/preservation and real xmin conflicts. In-memory tests own filename grammar, safe reads/deletion, missing-file races, LastAccessed throttling and DB-first LRU accounting; proxy tests use fake HTTP for stale serving and coalescing. No external image origin or M6 instance is involved.

Then run the ordinary non-Playwright/non-SpatiaLite suite with PostgreSQL attached, `dotnet build`, `bash -n deployment/install.sh deployment/deploy.sh`, deployment/config safety tests, and both Code Guard scopes. Production-like smoke launchers that previously isolated only `CacheSettings__ImageCacheDirectory` must also set an owned `Storage__CacheRoot`; `tools/trip-editor-asset-smoke.mjs` does so. Do not let disposable new writes fall back to `/var/cache/wayfarer/images`. These are disposable qualification results, not a production migration or host-ownership acceptance claim.


## Thumbnail and operational log storage qualification

Use disposable Storage roots for #625. Focused `TripThumbnailStorageTests`,
`TripMapThumbnailGeneratorTests`, `PublicTripImagesTests`, `OperationalLogStorageTests`,
`AdminLogsControllerTests`, `JobTests`, `StoragePathsTests` and `DeploymentScriptTests`
cover external JPEG persistence/atomic failure, authoritative HTTP misses, direct
MapSnapshot, daily file sink/Admin/cleanup convergence, platform defaults and native
routing/preparation. Capture overrides prove persistence without external network or
browser installation. Fixtures never write thumbnails into repository webroot.

Run the ordinary PostgreSQL-attached suite described above, build, relevant asset
smoke, shell syntax/config checks and both Code Guard scopes. Real-host launchers
must set their owned `Storage__LogRoot` and `Storage__CacheRoot`; the published asset,
waypoint and shared-layout runners retain their existing cleanup/evidence ownership.
These subsystem checks do not qualify browser-runtime relocation, a fully read-only
application root, containers, or the real production migration.

## F1 to F2 Data Protection qualification

Run the focused selection with the guarded PostgreSQL attachment configured:
`dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj --filter 'FullyQualifiedName~StableIdentity|FullyQualifiedName~DataProtectionCliTests|FullyQualifiedName~DataProtectionKeyRingTests|FullyQualifiedName~DeploymentScriptTests|FullyQualifiedName~PersonalLocationProviderFoundationTests'`.
The tests use disposable migrated databases, persistent disposable key rings and
real hosted content-root discriminators. They cover stable portability, source
rollback, in-memory replacement atomicity, startup consistency, bounded CLI
output, table write exclusion, xmin conflicts, idempotence, and transaction
rollback after protection/save failures. Executable CLI tests prove routing exits
before web startup or seeding. Secret comparisons intentionally use boolean
assertions so failure diagnostics cannot print credentials or ciphertext.

F2 additionally qualifies actual cookie-handler authentication, antiforgery requests,
Identity reset tokens, all four explicit operation purposes, stable-only activation,
legacy downgrade cutoff, API hash continuity, default-ring ambiguity and installed
service override preservation. These are disposable framework/PostgreSQL evidence,
not production M6 or provider-network qualification.

## Published read-only runtime qualification

`PublishedReadOnlyRuntimeTests` is the Linux Slice G smoke. It copies an existing
Release publish into its owned fixture, removes every write permission, proves a
write is denied to the running identity, then starts the actual Production host.
It uses the established disposable PostgreSQL migration fixture, external Storage
roots and an explicit external key ring. Startup enforces F2's stable `Wayfarer`
identity. The smoke requests the login page, a static asset and the real public
map-snapshot endpoint, verifies JPEG output in external thumbnail storage, external
logs and key XML, stops the host, and compares every publish entry and file digest.
No production database, key ring or native deployment is touched.

Provision the release-matched browser and OS libraries first using the deployment
guide. The ordinary .NET browser tests use the same external bundle. The historical
`libasound.so.2` launch failure requires `libasound2t64` on Ubuntu 24.04, not another
browser download. Run as an ordinary user; root bypasses permission evidence.

```bash
dotnet tool restore
dotnet frontend build
npm run build
dotnet publish Wayfarer.csproj -c Release -o /tmp/wayfarer-publish-qualification
# Use the dedicated PostgreSQL 17 fixture connection configured for ordinary tests.
export PLAYWRIGHT_BROWSERS_PATH="$HOME/.cache/ms-playwright"
export WAYFARER_TEST_PUBLISH_DIRECTORY=/tmp/wayfarer-publish-qualification
export WAYFARER_TEST_ARTIFACT_DIRECTORY=/tmp/wayfarer-read-only-evidence
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj \
  --filter 'FullyQualifiedName~PublishedReadOnlyRuntimeTests'
```

The selected test requires `WAYFARER_TEST_POSTGRES_CONNECTION` naming exactly
`wayfarer_import_tests`; it creates and drops only its own generated database.
A missing Linux/publish prerequisite is an explicit skip, not qualification.
The ordinary PostgreSQL-attached suite separately proves import and cache writes
at their stable storage seams. Browser-policy tests preserve launch options and
prove one bounded failure without retry; deployment guards reject runtime
installer calls in all current consumers. Run the browser category for existing
PDF/attribution and screenshot evidence, then the built-asset smoke and both Code
Guard scopes. Final Docker packaging and real native migration remain #603/#604.
