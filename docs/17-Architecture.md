# Architecture

Stack
- ASP.NET Core MVC (.NET 9)
- Entity Framework Core + Npgsql + NetTopologySuite (PostGIS)
- Quartz (persistent scheduler)
- Serilog (console, file, PostgreSQL)
- Razor views + Areas; Leaflet frontend with vanilla JS

High‑Level Components
- Areas: `Admin`, `Api`, `Identity`, `Manager`, `User`, `Public`
- Controllers: HTTP endpoints for MVC and API
- Services: application logic (imports, exports, geocoding, tiles, SSE)
- Parsers: file format ingestion
- Jobs: background processing via Quartz
- Util: helpers, roles, time, distance, HTML
- Middleware: performance logging, dynamic request size

Data Flow Examples
- Location Import: Upload -> `LocationImport` row -> Quartz job `LocationImportJob` -> `LocationImportService` parses batches -> optional reverse geocoding -> DB insert -> SSE progress
- Trip Export: Controller -> `ITripExportService` -> KML or PDF stream -> HTTP response
- Mobile SSE: `SseService` channels broadcast status and import progress to connected clients

Role Model
- `Admin`, `Manager`, `User` roles (see `Util/ApplicationRoles.cs`). RBAC used across Admin/Manager areas and API.
