# Testing

Approach
- Add xUnit tests under `tests/Wayfarer.Tests` (recommended structure).
- Focus on Services and Parsers for unit tests; add integration tests for critical flows (imports, trip exports, API auth).

Running Tests
- `dotnet test`
- Trip Editor E2E: `npm run test:e2e:trip-editor`

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

