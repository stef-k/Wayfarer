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

In GitHub Actions, the existing ordinary Release invocation always receives this
connection when `run_dotnet=true`. Its native service uses the upstream Debian
[`postgis/postgis:18-3.6`](https://hub.docker.com/r/postgis/postgis) image, pinned by
digest in `tests.yml`. The workflow checks healthy TCP readiness, the exact database
name, PostgreSQL major 18, PostGIS 3.6 and the administrative permissions the existing
extension/migration fixtures need. Fixtures apply the current migrations themselves.
The job also installs `postgresql-client-18` from PostgreSQL's signed Ubuntu apt
repository and selects its binaries ahead of the runner defaults. The prerequisite
check verifies and reports both client versions: the existing CLI database-copy test
needs host `pg_dump`/`pg_restore` compatible with server major 18.
The fixed `wayfarer_ci` superuser uses password-free trust only in this disposable
service, with the host port bound to `127.0.0.1`; no password enters initialization
logs, application configuration or artifacts. Never reuse this authentication setup
for a persistent database.

The same ordinary test discovery and Spatialite/Playwright/root exclusions apply;
there is no relational test-name allowlist or fixed count. The
`ordinary-test-results` artifact retains `ordinary.trx`, including pass/skip details.
A missing result or a skipped result whose reason mentions PostgreSQL, PostGIS or
the guarded database fails the job. Unrelated intentional skips remain permitted.
After the run, read-only checks reject leaked `wayfarer_migration_tests_*` databases
and fixture-owned users, and report the applied migration count. They never widen
fixture cleanup authority.

When `run_dotnet=false`, the native service's image expression resolves to an empty
string, so GitHub starts no PostgreSQL service. Documentation-only PRs still run
classification and Code Guard. PostgreSQL adds no recurring image, Compose,
operator, recovery or ARM64 qualification; editing the workflow itself retains the
existing one-time broad gate self-qualification. Measure the hosted `Initialize
containers`, `Run ordinary tests`, `test` job and complete workflow durations when
changing this wiring; these distinguish database startup and suite cost from the
separate broad qualification. Stop for a maintainer decision if ordinary CI adds
over roughly eight minutes or threatens its existing 30-minute job timeout.

Never point guarded fixtures at the normal `wayfarer` development database or at production. The fixtures validate the database name and use separate disposable schemas/databases where a test requires destructive migration ownership.

On Linux/WSL, attach a privately stored connection to the test process without committing or echoing it:

~~~sh
export WAYFARER_TEST_POSTGRES_CONNECTION='Host=localhost;Port=5432;Database=wayfarer_import_tests;Username=...;Password=...'
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj --filter 'Category!=RequiresSpatialite&Category!=RequiresPlaywright'
~~~

On Windows, the same variable can be stored at user scope and copied into the current process before the test run.

Use relational tests when the claim depends on migrations, Npgsql/PostGIS translation, database constraints, transactions, concurrent writers or persisted workflow state.

Historical migration tests may use one outer transaction for rollback isolation.
In that transaction, reapply only the migration being qualified: unrelated later
migrations can retain `ON COMMIT DROP` temporary tables until the outer transaction
ends. Check repaired historical data at the target revision, then upgrade to the
current schema before exercising current controllers and services. In the shared
migration fixture, compare audits before and after the operation instead of assuming
unrelated scenarios leave the entire audit table empty.

### Location-import generation and recovery evidence

`LocationImportQuartzPostgresTests` exercises persisted historical job rejection,
bounded projection repair and the real worker across scheduler reconstruction.
`RealWorker51Rows_InterruptedAfter50_ReconstructsAndResumesWithoutStaleWrites`
commits 50 rows and their checkpoint, interrupts Quartz through the existing batch
observer, reconstructs the worker/scheduler graph and resumes in a new generation.
It verifies 51 distinct committed identifiers, checkpoint 51, zero duplicate skips
and no writes by the previous generation. This is controlled interruption evidence;
it does not qualify a hard process kill.

The compatibility `ProcessImport` contract requires an explicit epoch. Missing,
invalid or identity-conflicting Quartz payloads cannot enter the worker or lifecycle.
Current job keys must match both payload numbers; recognized historical one-number
keys must match the import ID and still supply an explicit epoch. Explicit zero
authorizes only durable zero. `LocationImportBoundedPostgresTests` covers rejection
through both worker entry points and preservation of transactional deduplication/savepoints.

The same PostgreSQL fixture covers legacy checkpoint compatibility through the real
worker, `LocationImportJob` and fenced terminal convergence. A saved total/checkpoint
of 51/50 against 50 newly yielded records fails without changing progress or history,
including on repeated Start. A new checkpoint-zero import recovers the missing valid
UUID and preserves earlier owner edits; a matching nonzero checkpoint still resumes.
Missing/unusable totals and checkpoints beyond the new count also fail closed.

`CorruptTriggerLink_ReconcilesTwiceAndRealWorkerCompletes` first asserts a persisted
wrong link, repairs it with and without an existing current job, verifies idempotence
and completes through the real worker. Replacement preserves even a non-durable
unrelated job that would be deleted by removing its last trigger. Reconciliation
verifies job identity/payload and the trigger link before clearing pending intent;
`FailedOrUnverifiedTriggerRepair_RemainsPendingAndRetries` covers repair failure,
ineffective replacement and successful retry at the lifecycle contract seam.

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
bash deployment/check-trust.sh --node
export PLAYWRIGHT_NODEJS_PATH="$(command -v node)"
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
Unexpected supervisor errors include frame filenames, line numbers and function
names on stderr and in private `supervisor.log` diagnostics. Exception values,
source lines, directory paths and local variables are omitted.
Diagnostic file writes require verified root ownership and pinned directory/file
descriptors, rejecting links and replaced targets. Unsafe storage or platforms
without descriptor-relative, no-follow opens (including Windows) use sanitized
stderr only and report the retention failure without interrupting cleanup.

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

The repository currently pins Agent Code Guard 0.5.1. Install that published version in an isolated environment such as pipx:

~~~sh
pipx install agent-code-guard==0.5.1
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

### Personal history reads (#918 I918-B/C)

Use the existing guarded PostgreSQL 18/PostGIS prerequisite above. The focused
selection is `HistoryQueryTests`, `HistoryCursorTests`, `HistoryPredicatePostgresTests`,
`LocationHistoryHttpPostgresTests` and `HistoryQueryPlanPostgresTests`. Pure tests own
transport/wall boundary and cursor rules; direct SQL tests own the semantic matrix;
real MVC/PostgreSQL tests own bearer authority, snapshots, coverage, independent
overview/detail, preparation gates, live paging, protected errors and cancellation.
No browser or provider is needed. The disposable migration fixture owns its database.

The plan probe prepares 10,000 distinct synthetic records through I918-A, then
records parameterized production predicates, natural EXPLAIN ANALYZE/BUFFERS for
UTC/viewer/capture/text/geometry and whole-scope uncertainty, plus separately labelled
forced index eligibility. Its actual executed overview plan also checks that
grouping uses compact numeric coordinates instead of expanding PostGIS validation
and conversion into every edge comparison. `OverviewRetainsEveryCellAcrossAllSupportedGridSizes`
checks every occupied cell, decoded bounds/order, singleton and unmapped accounting
at each supported grid from 1 through 16, and rejects a grid beyond that bound.
The nullable digits fit radix 17 and keys 0–288 in PostgreSQL's integer type.
These checks make no endpoint timing or capacity assertion. Retain
detailed xUnit output with `--logger 'console;verbosity=detailed'`.

`CollectionReadinessRetainsAllOwnerAndSnapshotGuarantees` checks the request gate
against another owner's stale generations, terminal unresolved evidence, scanning
checkpoints, writer identity, missing physical indexes and a concurrent edit across
a repeatable-read snapshot. Operator totals remain separately covered by the
existing preparation tests.

I918-C extends the existing routed fixture, without a browser or a benchmark
framework. `IndependentOverviewRetainsDistantAndCoincidentRecords` qualifies the
map-first journey: full-period dense home plus sparse destinations, repeated
refinement using the exact returned bounds, bounded coincident choices, direct
detail, then independent chronology pages with the same scope/identities.
`CompleteDetailPreservesFieldsAndClassifiesUnrepresentableValues` checks full
multilingual Notes (873,632 UTF-8 bytes), all address/provenance fields, original
identity/clocks and known-null versus unavailable publication. The existing
`CandidateCoverageAndPendingPreparationAreDifferent` owns incomplete coverage,
candidate inspection, preparation gates and unaffected reads.

With the guarded connection attached, reproduce those focused product contracts:

~~~sh
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj \
  --filter 'FullyQualifiedName~LocationHistoryHttpPostgresTests' \
  --logger 'console;verbosity=detailed' --logger trx
~~~

The retained I918-C scale receipt is separate from ordinary CI. It uses the same
guarded disposable migration fixture and actual registered MVC routes, with
210,240 and 1,051,200-record owners plus 210,240 unrelated-owner records. Its
single-purpose probe/source, exact commands, raw response examples, stage timings,
parameterized SQL/plans and failed attempts are preserved under
`/home/stef/.local/share/wayfarer-investigations/918-20261024/i918-c/`.
The corrected scale receipt is in `qualified/`; superseded root-level attempts
are explicitly excluded from agreed full-period qualification. The receipt's
`commands.md` explains reproduction; only its project-reference
checkout path needs adjustment. It requires the established guarded connection,
not a production database. Scale seeding generates prepared synthetic values and
checks representative samples against production derivation; it is not backfill
throughput. The independent I918-A preparation/write/storage trial is reused.

Report routed request duration/bytes separately from companion calls to serving
production preparation/query/projection and JSON serialization stages. EF
execute-reader/drain timings are client observations; natural EXPLAIN ANALYZE is
separately replayed database execution, not a subtractable component of that same
request. Raw Npgsql detail bypasses EF interception. Cache/JIT, Debug build,
background activity and in-process TestServer transport must remain explicit.
Neither a bounded response nor fast first page qualifies complete overview work.

The bounded performance correction repeats the retained workload in
`/home/stef/.local/share/wayfarer-investigations/918-performance-20261024/`.
Its `before/` and `after/` use identical probe/seed source and fresh guarded databases;
`commands.md` records conditions and reproduction. Each contains exactly 158 unique
intact routed observations. Receipt appends are serialized, and all five four-read
rounds exclude diagnostic replays. Keep the older collision/recovery and failed
results as historical evidence. Compare complete routed requests with complete
routed requests; separate stage calls and natural plan replays retain their own
labels. Bounded 250 ms application/whole-cluster CPU/RSS sampling includes an explicit
shared-page caveat for summed PostgreSQL RSS. Report the under-one-second target's
successes and misses separately from five-second cancellation success.

The compact-cell follow-up retains the read-only query-shape investigation at
`/home/stef/.local/share/wayfarer-investigations/918-query-shape-20261024/`.
Its full response/filter/detail and five four-read comparisons remain reusable;
the compact key did not demonstrate a concurrency improvement. The bounded
production repeat is in
`/home/stef/.local/share/wayfarer-investigations/918-compact-cell-20261024/`:
broad, recurring/common-text and combined Tokyo overviews at both retained scales,
plus the same 500-row post-write control. It compares unchanged production SQL
with only the previous two-key grouping reconstructed on the same guarded fixture.
Do not repeat the unaffected broad matrix or attribute whole-cluster CPU/RSS to
these queries. Keep raw plan temporary I/O, query time and routed time separate;
the five-second deadline and every match/filter/readiness rule remain unchanged.

The deferred singleton UUID follow-up retains its bounded delivery receipt at
`/home/stef/.local/share/wayfarer-investigations/918-singleton-delivery-20261024/`.
It reuses the research seed and selects broad and recurring/common-text overviews
at both scales, plus one large-owner post-write broad control. Candidate commands
are unchanged compiled production SQL; the baseline aggregate is reconstructed
and checked against the retained pre-change production SQL before execution.
Use `qualified/` for the comparable results; the preserved root attempt failed
its baseline reconstruction check on a missing final newline. The corrected
driver verifies all five SQL shapes before creating the replacement fixture.
Three routed observations, one separate query/projection/serialization companion
and one natural plan per variant/scope retain complete response parity and resource
evidence. The wider workload, preparation/write/storage and four-read concurrency
receipts are explicitly reused; no new concurrency improvement is claimed.

`OverviewSingletonIdentityKeepsNullsAndOwnerIsolation` checks real singleton
IDs/nullable UUIDs, a foreign owner sharing a portable alias, multi-record cells
and unmapped accounting. All-grid checks include null singleton UUIDs. The routed
snapshot test now also deletes a singleton after the response snapshot is taken,
then verifies that both its ID and UUID remain visible together. Exact shared-edge,
drill-down, uncertainty, readiness, cancellation and deadline regressions remain
in the focused selection above. This incremental optimization does not complete
I918-C or waive its measured performance target misses.

### Final history operating-range qualification (#918)

The approved collection evaluation ceiling is now **15 seconds** for both history
and overview; detail keeps its separate 30 seconds. The remaining-time database
timeout stays local to the read transaction. Admission and disconnect cancellation
are unchanged. `CollectionsCanCompleteAfterFormerDeadline` exercises both real
routes with six seconds of database work; the deadline regression verifies a
controlled 503 near fifteen seconds without a partial success. Earlier reports'
five-second settings are historical, not the current ceiling.

The [final bounded qualification](history-final-qualification.md) records the new
2,628,000-record owner, equivalent-selectivity text selections on the shipped
Notes/address OR predicate, warm/post-write observations, ordinary reads and durable
location ingestion alongside heavy searches. Reuse the retained single-purpose
routed fixture/receipt; this is a local qualification, not a CI benchmark, load
framework or 25-active-user capacity claim. Keep the sub-second target, the
protective ceiling and the maintainer acceptance decision distinct.
