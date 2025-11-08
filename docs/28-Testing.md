# Testing

Approach
- Add xUnit tests under `tests/Wayfarer.Tests` (recommended structure).
- Focus on Services and Parsers for unit tests; add integration tests for critical flows (imports, trip exports, API auth).

Running Tests
- `dotnet test`

Targets
- Parsers: sample fixtures for GPX/KML/CSV/GeoJSON/Google JSON.
- Services: `LocationImportService`, `TripExportService`, `ReverseGeocodingService` (mock external calls).
- API: controller tests using `WebApplicationFactory` and in‑memory DB or test containers.

Guidelines
- Keep tests focused and deterministic.
- Avoid real secrets and external network calls in tests.

