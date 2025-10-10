# Repository Guidelines

## Project Structure & Module Organization

- ASP.NET Core MVC (.NET 9) with PostgreSQL + PostGIS.
- Entry: `Program.cs`; project: `Wayfarer.csproj`.
- Areas: `Areas/Admin`, `Areas/Api`, `Areas/Identity`, `Areas/Manager`, `Areas/User`, `Areas/Public`.
- Views: `Views/**` and per‑area Razor views.
- Services: `Services/**`, Jobs: `Jobs/**`, Parsers: `Parsers/**`, Utilities: `Util/**`.
- Static assets: `wwwroot/**` (e.g., `lib/`, `css/`, images; JS prefers area‑aligned modules under `wwwroot/js`).

## Project Paths

- Backend (this repo): `C:\Users\stef\source\repos\Wayfarer`
- Mobile app (separate repo): `C:\Users\stef\source\repos\Wayfarer.Mobile`

## Development Environment

- Primary dev OS: Windows 10; install .NET 9 SDK.
- Database: PostgreSQL with PostGIS. Configure via `ConnectionStrings:DefaultConnection`.
- Front end: plain modern JavaScript (prefer arrow functions).
- Maps: Leaflet with OpenStreetMap tiles and local cache. Configure cache directories under `CacheSettings:*` in `appsettings*.json`.

## Build, Test, and Development Commands

- `dotnet restore` – restore NuGet packages.
- `dotnet build` – compile the web app.
- `dotnet run` – run locally (loads `appsettings.Development.json` if present).
- `dotnet watch run` – hot‑reload during development.
- Admin CLI: `dotnet run -- reset-password <username> <new-password>`.

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

## Commit & Pull Request Guidelines

- Clear, imperative commits. Conventional Commits welcome (e.g., `feat(trips): ...`, `chore: ...`).
- PRs must include: description, linked issues, screenshots for UI changes, test plan/steps, and DB migration notes when relevant.

## Security & Configuration Tips

- Configure `ConnectionStrings:DefaultConnection` in `appsettings*.json`. Requires PostGIS.
- Ensure `Logging:LogFilePath:Default` exists; tile/cache paths under `CacheSettings:*`.
- Reverse proxy: forwarded headers configured in `Program.cs`; adjust for your environment.
- Keep API tokens/secrets out of Git; use user‑secrets or environment variables.
