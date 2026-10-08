---
title: Testing
---

# Testing

Prove each requirement at the lowest reliable seam. A focused service/controller/client test is preferable to a browser workflow when it owns the behavior directly; PostgreSQL and browser evidence are added when the requirement actually crosses those boundaries.

Repository merge/review policy is owned by [AGENTS.md](../../AGENTS.md). This page describes evidence and commands, not the implementation workflow.

## Focused .NET tests

The main test project is `tests/Wayfarer.Tests/Wayfarer.Tests.csproj`. Start with a class/name filter that exercises the changed owner, then broaden only as needed.

An ordinary non-browser run is:

~~~sh
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj --filter 'Category!=RequiresSpatialite&Category!=RequiresPlaywright'
~~~

Tests that opt into PostgreSQL may still skip when the guarded database is not attached. A skip is evidence that the prerequisite was unavailable to that process, not evidence that the behavior passed.

Mocked/InMemory/controller tests prove their own seam. They do not automatically prove PostgreSQL constraints, transactions, migrations or PostGIS behavior.

## Coverage reports

From the checkout, use Python 3.12+ and the installed .NET SDK:

~~~sh
python3 tools/coverage_report.py
~~~

On Windows, use `python tools/coverage_report.py`. The utility restores the pinned
.NET tools, builds Debug tests, runs ordinary tests with `coverlet.runsettings`,
and consumes exactly one valid Cobertura XML to generate a nonempty HTML report.
Spatialite, Chromium and privileged setup selections remain separate.

Open the printed `coverage-report/<GUID>/index.html`. Each invocation creates the
same fresh lowercase GUID child beneath the report and test-results roots. It
removes only current-run results and, on failure, its own partial report. Earlier
reports survive failure; after a validated success, only ordinary GUID report
siblings are pruned. Unknown files and linked/reparse trees are preserved. Links
in any artifact ancestor or descendant and replacement directories fail closed.
Cleanup errors are reported separately and never replace a primary tool exit code.
The CLI accepts no caller-selected output or deletion paths.

## PostgreSQL and PostGIS tests

Maintainer/guarded relational evidence uses PostgreSQL 18 with PostGIS and the dedicated persistent database named exactly `wayfarer_import_tests`. Attach it with `WAYFARER_TEST_POSTGRES_CONNECTION`.

If that connection is configured, the connected server must report PostgreSQL major 18. An unsupported configured major fails the guarded prerequisite rather than counting as relational evidence. An absent connection retains the opt-in skip behavior.

Never point guarded fixtures at the normal `wayfarer` development database or at production. The fixtures validate the database name and use separate disposable schemas/databases where a test requires destructive migration ownership.

On Linux/WSL, attach a privately stored connection to the test process without committing or echoing it:

~~~sh
export WAYFARER_TEST_POSTGRES_CONNECTION='Host=localhost;Port=5432;Database=wayfarer_import_tests;Username=...;Password=...'
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj --filter 'Category!=RequiresSpatialite&Category!=RequiresPlaywright'
~~~

On Windows, the same variable can be stored at user scope and copied into the current process before the test run.

Use relational tests when the claim depends on migrations, Npgsql/PostGIS translation, database constraints, transactions, concurrent writers or persisted workflow state.

## Client and frontend checks

`package.json` owns the current frontend commands:

~~~sh
npm run typecheck
npm run test:client
npm run build
~~~

`npm run typecheck` checks the Vue/TypeScript client, `npm run test:client` runs the Node client tests, and `npm run build` produces the Vite Trip Editor assets. Run the narrower named client scripts when they are the exact owner of a change.

MvcFrontendKit/Razor asset changes may also require:

~~~sh
dotnet tool restore
dotnet frontend build
~~~

## Browser and Playwright evidence

Wayfarer has both .NET Playwright capture tests and JavaScript Playwright user-journey tests. Before declaring Chromium unavailable, check the established Playwright cache/install path and the version-coupled installation command in the built .NET application. The in-app browser, when available, is not the only supported browser path.

A typical .NET Playwright prerequisite after a Release build is:

~~~sh
dotnet build tests/Wayfarer.Tests/Wayfarer.Tests.csproj -c Release
dotnet tests/Wayfarer.Tests/bin/Release/net10.0/Wayfarer.dll playwright install chromium
~~~

The explicit `playwright` command calls the
[documented Playwright .NET API](https://playwright.dev/dotnet/docs/browsers#install-browsers-via-api)
from the same package as the application/tests, returns its exit status, and exits
before web startup, configuration, database access or logging. For an installation
preview append `--dry-run`; for Linux OS libraries use
`sudo dotnet tests/Wayfarer.Tests/bin/Release/net10.0/Wayfarer.dll playwright install-deps chromium`.
`install --with-deps chromium` combines both steps when run with the required
privileges. Set `PLAYWRIGHT_BROWSERS_PATH` to the same directory for installation
and test execution if using a private cache; otherwise Playwright uses its normal
per-user cache. Windows uses the same `dotnet` command from Command Prompt.
No PowerShell installation is required. Any vendor-generated `.ps1` is unused.

CI validates exactly one generated `browsers.json` and numeric Chromium revision
with `tools/playwright_metadata.py`, retaining the OS/revision cache key and browser
path. Python coverage/browser filesystem tests and Node cleanup tests run on Linux
and Windows:

~~~sh
python3 -B -m unittest discover -s tools/tests -v
npm run test:cleanup:safety
~~~

For JavaScript Playwright:

~~~sh
npx playwright install chromium
~~~

Managed browser journeys use one Python supervisor (Python 3.12+ on Linux/WSL or
Windows), the guarded PG18 connection above, Node/npm and installed Chromium:

~~~sh
npm run test:e2e:shared-layout
npm run test:e2e:shared-layout -- --grep 'connect apps'
npm run test:e2e:waypoint
python3 -B -m unittest discover -s tools/tests -v
~~~

The supervisor copies source/build output into a private UUID temporary root,
verifies the effective application connection, prepares the maintained schema,
and records exact fixture IDs before inserting missing Identity prerequisites.
Existing ActivityTypes are required. It seeds a run-owned User and public sample
Trip for shared-layout; token replacement affects that User only. Human credentials
and `.local/manual-verification.md` are never used by the managed mutation profile.
All four Storage roots and legacy caches are private to the run. The selected
Data Protection ring is copied into private storage with its stable application
identity; existing keys and protected credentials must remain readable. The
maintainer's key ring is never written by the managed host.

Shared-layout retains a Development HTTPS host on 7150 (an installed .NET
development certificate is required) and its owned Vite server on 5173. Both ports
must be free. Waypoint retains a published Production host and the historical
two-spec aggregate/route-work selection with real C# provider rereads. Runs use
one worker and zero retries. Playwright owns assertions and browser installation;
reusable Chromium caches stay outside disposable roots.

Python stops and reaps its owned host, Node/browser and helper children, verifies
endpoint release, runs fixture cleanup and a separate verification, then deletes
only its exact owned root. Cancellation uses the same finalizer. Primary failure
status is preserved if cleanup also fails. Unproved ownership retains marked
residue; no stale process/root is adopted. Private failed-run logs are retained
under `.local/browser-e2e/<run UUID>`. Managed traces/videos and shared-layout
automatic failure screenshots are disabled because pairing/typing can reveal
credentials; the existing safe post-hide screenshot remains local.

`npm run test:e2e:trip-editor` remains the externally attached mode. The distinct
Lifecycle spec's real database outage requires an independently owned disposable
server; it is outside the managed waypoint selection. Never point its stop controls
at the maintained PG18 service. Coverage reporting remains separate from the
managed browser supervisor.

Keep browser proof bounded: normally one critical happy-path smoke and, only when risk warrants it, one focused negative/race observation. Do not reproduce every role, lifecycle, viewport, provider or persistence permutation in one browser workflow when lower seams already own them.

A browser fixture, locator, port, cache or setup failure is infrastructure evidence, not automatically a product defect. Diagnose the prerequisite and allow at most a bounded rerun. If browser evidence remains unavailable, report that honestly rather than calling it a pass.

## CI classifier

`tools/ci/application_image_scope.py` classifies the exact PR base/head diff into evidence-owner domains. Current domains include `dotnet`, `playwright`, `frontend`, `cleanup_safety`, `release_tooling`, `app_image`, `db_compose`, `operator`, `recovery`, `update` and `arm64`; some domains add explicit execution prerequisites for others.

The classifier is path-owner based and fail-broad when it cannot obtain a valid exact diff. Inspect a real diff locally with full SHAs:

~~~sh
python3 -B tools/ci/application_image_scope.py --base <full-base-sha> --head <full-head-sha>
~~~

Ordinary Markdown documentation has no product evidence owner (except executable release inputs such as the changelog/release installer documentation), so a documentation-only PR should take the cheap CI path. If a docs-only diff unexpectedly selects product .NET/frontend/browser/image/Compose qualification, diagnose the changed path/classifier result instead of accepting unrelated expensive work.

## Code Guard

The repository currently pins Agent Code Guard 0.5.0. Install that published version in an isolated environment such as pipx:

~~~sh
pipx install agent-code-guard==0.5.0
code-guard --version
code-guard doctor --json
~~~

`doctor` must report healthy installation, configuration, Git, skill and parser providers.

During development, inspect the changed scope:

~~~sh
code-guard . --changed-only --json --json-mode compact
~~~

Before completion, inspect the complete branch relative to `main`:

~~~sh
code-guard . --base-ref main --ci
~~~

`code-guard . --ci` without a base ref is a deliberate full-tree audit, not the normal branch check.

Interpret results rather than suppressing them:

- `PASS`: no blocking finding; normal exit 0.
- `REVIEW`: inspect every finding and either improve the change or record why the finding is acceptable. Normal mode exits 1; `--ci` keeps REVIEW visible but exits 0.
- `FAIL`: blocking finding, exit 2.
- `INCOMPLETE` or tool/configuration failure: evidence is incomplete and completion is blocked, exit 3.

`doctor` uses its own status contract: healthy 0, unhealthy 1, invocation/internal error 3. CI also runs the pinned Code Guard gate for documentation-only PRs.

Code Guard complements compilers, tests, linters, security analysis and review; it does not replace them.

## Claim boundaries

- A skipped or unavailable check is not a pass.
- Client/mocked tests do not prove persistence unless they execute the relevant database seam.
- An InMemory EF test does not prove PostgreSQL/PostGIS translation or constraints.
- Browser infrastructure failure is not automatically a product failure.
- A green broad suite does not replace a focused regression proof when a practical seam exists.
- Do not add higher-layer evidence merely to duplicate behavior already proven at the owning lower seam.
