# Repository Guidelines

## Project Structure & Module Organization

- ASP.NET Core MVC (.NET 10) with PostgreSQL + PostGIS.
- Entry: `Program.cs`; project: `Wayfarer.csproj`.
- Areas: `Areas/Admin`, `Areas/Api`, `Areas/Identity`, `Areas/Manager`, `Areas/User`, `Areas/Public`.
- Views: `Views/**` and per‑area Razor views.
- Services: `Services/**`, Jobs: `Jobs/**`, Parsers: `Parsers/**`, Utilities: `Util/**`.
- Static assets: `wwwroot/**` (e.g., `lib/`, `css/`, images; JS prefers area‑aligned modules under `wwwroot/js`).

## Project Paths

- Backend (this repo): `C:\Users\stef\source\repos\Wayfarer`
- Mobile app (separate repo): `C:\Users\stef\source\repos\WayfarerMobile`

## Development Environment

- Primary dev OS: Windows 10; install .NET 10 SDK.
- Database: PostgreSQL with PostGIS. Configure via `ConnectionStrings:DefaultConnection`.
- Front end: plain modern JavaScript (prefer arrow functions).
- Maps: Leaflet with OpenStreetMap tiles and local cache. Configure cache directories under `CacheSettings:*` in `appsettings*.json`.

## Build, Test, and Development Commands

- `dotnet restore` – restore NuGet packages.
- `dotnet build` – compile the web app.
- `dotnet run` – run locally (loads `appsettings.Development.json` if present).
- `dotnet watch run` – hot‑reload during development.
- Admin CLI: `dotnet run -- reset-password <username> <new-password>`.
- Maintainability check: `code-guard . --changed-only --json --json-mode compact` (Agent Code Guard 0.3.1).

## Coding Style & Naming Conventions

- Document all code you touch or add (XML docs C#, comments Razor/JS).
- Never create files unless absolutely necessary; prefer editing existing ones.
- Prefer minimal services/classes/methods/variables; keep scope tight.
- C#: 4 spaces; PascalCase for types/properties; camelCase for locals/params; file name = primary type.
- JS: modern style, prefer arrow functions; keep modules area‑scoped when applicable.

## Testing Guidelines

- Use the existing xUnit project under `tests/Wayfarer.Tests`; name focused tests `*Tests.cs`.
- Follow the test pyramid: pure/unit tests own algorithms and state matrices; component tests own reactive UI transitions; focused PostgreSQL tests own persistence, locking, and recovery; Playwright owns only behavior that materially requires a mounted browser.
- Keep browser evidence proportionate: normally one critical happy-path smoke and, only when the risk warrants it, one focused negative/race observation. Do not encode exhaustive lifecycle, role, viewport, provider, or failure matrices as one uninterrupted browser workflow.
- Prove each requirement at the lowest reliable seam. Do not repeat a state-transition matrix in Playwright when deterministic client/component tests already exercise the production state owner, or repeat persistence matrices in the browser when focused relational tests cover them.
- A browser fixture, locator, host, port, timing, or setup failure is test-infrastructure evidence, not a product defect. Diagnose and correct it once, then allow at most one full rerun of the same selection. Do not perform a third environment rebuild unless the preceding run exposed a concrete product failure.
- A missing opt-in environment variable or browser-backend attachment is not proof that PostgreSQL or Chromium is unavailable. Before reporting infrastructure as unavailable, inspect the documented persistent test database, installed PostgreSQL tools/service, repository provisioning scripts, Playwright caches, and version-coupled installers. Provision or repair the established reusable prerequisite when it is safe and in scope; do not stop at the first missing variable.
- An unavailable in-app browser connector is not evidence that Chromium is unavailable. Try the in-app browser once and follow its documented troubleshooting once; if it remains unavailable, use the repository's established bundled Playwright Chromium, authenticated test convention, version-matched installer, and configured browser cache. Do not report browser evidence unavailable until both supported paths have failed, and do not create a bespoke browser harness as a substitute.
- When browser infrastructure remains unavailable or fails again without a product counterexample, report the browser evidence as unavailable/validation debt and make the readiness decision from the remaining risk and evidence. Missing browser evidence blocks readiness only when the changed user-visible behavior cannot be credibly exercised at a lower stable seam.
- Do not let test-harness refinement displace the production fix. If the same setup gap recurs across issues, improve or document the shared harness in a dedicated slice instead of rebuilding bespoke infrastructure inside each product issue.
- Design issue acceptance criteria with these limits from the outset. “One uninterrupted workflow” may cover the core journey, but must not become a cross-product of every transition and failure case.
- Run focused selections first. Run wider suites only when the touched shared seam makes wider regression plausible.

## Validation & PR Readiness

- Use the installed Code Guard skill and repository configuration after meaningful supported source or Markdown edits: `code-guard . --changed-only --json --json-mode compact`.
- Before completion, cover the complete branch change with `code-guard . --base-ref main --ci`; outside Git, pass the exact edited files. Full scans are deliberate audits.
- PASS continues normally. REVIEW requires inspection and a concise design justification or a genuine improvement, not automatic refactoring. FAIL, INCOMPLETE, and tool/configuration errors block completion unless an applicable explicit exception covers the finding; retain completed evidence.
- Load only the bundled policy guidance named in `requiredPolicies`. Report accepted reviews and the final result.
- All six guards retain shipped thresholds. LOC above 400 triggers review; above 600 fails unless covered by the committed non-increasing allowance. Exactly 400 passes and exactly 600 reviews.
- `.agent-tools/code-guard.loc-baseline.json` records existing files above 400 LOC at adoption. Normal analysis never updates it; explicit updates may only lower/prune allowances. Growth beyond a recorded allowance fails.
- Never game metrics with mechanical splitting, meaningless helpers, compressed formatting, added exclusions, changed thresholds, disabled guards, or baseline changes. See [Testing](docs/22-Testing.md#code-guard) for installation, exclusions, and CI behavior.
- When validation fails, classify each failure as either a current-branch regression or an out-of-scope pre-existing/cross-slice failure.
- Also classify fixture/environment failures separately; never convert them into product blockers without a production counterexample.
- Fix current-branch regressions before declaring a branch PR-ready.
- For out-of-scope failures, open or link a follow-up issue before merge, then mention that issue in PR, review, and merge notes.
- Do not claim the full suite is green until the follow-up is fixed and the full suite has been rerun successfully.
- A focused slice may proceed with explicitly reported validation debt when the product risk is already covered proportionately at stable lower seams; user approval is required only when the remaining untested risk is material, not merely because a disposable browser harness failed.

## Commit & Pull Request Guidelines

- Clear, imperative commits. Conventional Commits welcome (e.g., `feat(trips): ...`, `chore: ...`).
- PRs must include: description, linked issues, screenshots for UI changes, test plan/steps, and DB migration notes when relevant.
- Treat the GitHub Actions `test` check on the current PR head as the merge gate. Poll the actual check until it reports success, then merge; do not rely on `gh pr checks --required` or `gh pr merge --auto` unless branch protection and auto-merge enforcement have first been verified.
- Pending, failed, cancelled, or missing checks are not successful merge evidence. For a clear infrastructure stall, cancel and rerun the unchanged workflow at most once before reporting the infrastructure failure.
- Documentation-only PRs may skip the expensive test steps, but the required `test` job must still complete successfully through its documented fast path.

## Security & Configuration Tips

- Configure `ConnectionStrings:DefaultConnection` in `appsettings*.json`. Requires PostGIS.
- Ensure `Logging:LogFilePath:Default` exists; tile/cache paths under `CacheSettings:*`.
- Reverse proxy: forwarded headers configured in `Program.cs`; adjust for your environment.
- Keep API tokens/secrets out of Git; use user‑secrets or environment variables.
