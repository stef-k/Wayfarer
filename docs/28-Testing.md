# Testing

Approach
- Add xUnit tests under `tests/Wayfarer.Tests` (recommended structure).
- Focus on Services and Parsers for unit tests; add integration tests for critical flows (imports, trip exports, API auth).

Running Tests
- `dotnet test`

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

