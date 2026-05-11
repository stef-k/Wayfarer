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
- LOC policy check after implementation edits: `dotnet run --project tools/Wayfarer.LocCheck -- --warn 400 --fail 600`.

## Coding Style & Naming Conventions

- Always present simple and conhise action plan for aproval before generating or updating files and code. I need to approve plan before proceeding.
- Document all code you touch or add (XML docs C#, comments Razor/JS).
- Never create files unless absolutely necessary; prefer editing existing ones.
- Prefer minimal services/classes/methods/variables; keep scope tight.
- C#: 4 spaces; PascalCase for types/properties; camelCase for locals/params; file name = primary type.
- JS: modern style, prefer arrow functions; keep modules area‑scoped when applicable.

## Testing Guidelines

- No test project committed yet. Prefer xUnit in `tests/Wayfarer.Tests` with `*Tests.cs` naming.
- Focused unit tests for Services/Parsers; integration tests for critical flows.
- Run tests with `dotnet test` (once tests exist). Aim high coverage for changed code.

## Validation & PR Readiness

- New and changed source files are subject to the LOC policy check: warn at 400 counted LOC and hard fail above 600 counted LOC. Existing legacy files are grandfathered by `tools/Wayfarer.LocCheck/loc-baseline.json`, but should not grow past baseline.
- After any implementation edit, run `dotnet run --project tools/Wayfarer.LocCheck -- --warn 400 --fail 600`; summarize warnings and split files or request an explicit exception for failures.
- LOC checker interpretation:
  - 600 counted LOC is a hard cap unless the user explicitly approves an exception.
  - 400 counted LOC is a review trigger, not an automatic refactor command.
  - When a changed/new file warns, inspect whether the file is still cohesive and single-responsibility.
  - Decide whether the extra size is justified by necessary orchestration/simple structure, or whether the file is mixing separable responsibilities.
  - Consider likely near-term growth from upcoming slices before accepting a warning.
  - Split only when it improves responsibility boundaries or reduces meaningful complexity.
  - Do not split purely to satisfy the number if the split adds indirection without design benefit.
  - Report the decision as either "warning accepted with justification" or "split performed because ...".
- When validation fails, classify each failure as either a current-branch regression or an out-of-scope pre-existing/cross-slice failure.
- Fix current-branch regressions before declaring a branch PR-ready.
- For out-of-scope failures, open or link a follow-up issue before merge, then mention that issue in PR, review, and merge notes.
- Do not claim the full suite is green until the follow-up is fixed and the full suite has been rerun successfully.
- A focused slice may proceed with tracked validation debt only when that debt is explicitly accepted.

## Commit & Pull Request Guidelines

- Clear, imperative commits. Conventional Commits welcome (e.g., `feat(trips): ...`, `chore: ...`).
- PRs must include: description, linked issues, screenshots for UI changes, test plan/steps, and DB migration notes when relevant.

## Security & Configuration Tips

- Configure `ConnectionStrings:DefaultConnection` in `appsettings*.json`. Requires PostGIS.
- Ensure `Logging:LogFilePath:Default` exists; tile/cache paths under `CacheSettings:*`.
- Reverse proxy: forwarded headers configured in `Program.cs`; adjust for your environment.
- Keep API tokens/secrets out of Git; use user‑secrets or environment variables.
