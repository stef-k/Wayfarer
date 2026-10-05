---
title: Application architecture
---

# Application architecture

Wayfarer is one ASP.NET Core application with PostgreSQL/PostGIS persistence, Razor and JavaScript UI, a Vue/Vite Trip Editor, Quartz background work and Server-Sent Events (SSE). The useful architectural unit is an ownership seam: HTTP translation, domain/workflow behavior, durable state, background execution or client presentation.

## Startup and composition

`Program.cs` is the composition root. Before normal web startup it handles several command-line/maintenance paths, including version, Data Protection, recovery-source and lifecycle commands. Those paths exit before normal HTTP hosting; this keeps maintenance authority separate from ordinary web activation.

Normal startup then:

1. builds configuration and resolves `StoragePaths`;
2. configures Serilog, EF/Npgsql, Identity, Quartz and trusted proxy handling;
3. registers application services, frontend integration, Swagger and outbound HTTP clients;
4. builds the app and enforces the environment-specific database/readiness boundary;
5. validates the stable Data Protection authority;
6. maps health, area and default routes and applies middleware.

In non-Production environments, startup migrates EF, aligns Quartz tables and seeds required data. Production startup is read-only with respect to schema and administrator state and requires explicit preparation.

## HTTP areas

The `Areas/` folders separate HTTP responsibilities:

- `Admin` owns instance-wide administration such as settings, jobs, logs and user administration.
- `Manager` owns management operations delegated to the Manager role.
- `User` owns an authenticated user's Timeline, Trips, imports/exports, groups and personal provider settings.
- `Api` owns JSON endpoints, mobile-facing endpoints, Trip Editor endpoints and SSE.
- `Public` owns explicitly published or embeddable views and public supporting endpoints.
- ASP.NET Core Identity provides sign-in/account Razor Pages under the Identity area.

Controllers should translate HTTP concerns, authorize the caller, validate the request and delegate substantive behavior. Reusable domain/workflow logic belongs in services. Persistence belongs behind `ApplicationDbContext` and the service that owns the transaction or workflow.

## Persistence and durable ownership

`ApplicationDbContext` derives from `IdentityDbContext<ApplicationUser>` and is the main relational authority. Npgsql uses NetTopologySuite against PostgreSQL/PostGIS. The model includes Identity, locations, trips, groups, visits, settings, cache metadata, imports and provider/workflow state.

Durable relational state is distinct from filesystem state. `StoragePaths` provides one runtime authority for four categories:

- `DataRoot`: durable application data, including uploads and the default Data Protection key ring.
- `CacheRoot`: rebuildable tiles, proxied images and generated thumbnails.
- `LogRoot`: operational file logs.
- `TempRoot`: ephemeral working files.

Code that introduces filesystem state should use the existing owning storage service/path rather than constructing repository-relative paths.

## Request and workflow flows

A typical MVC/API flow is:

`request -> controller -> owning service -> ApplicationDbContext/external boundary -> response`

Authorization and ownership checks stay at the HTTP/service boundary that has the necessary identity and resource context. Services should not make controllers the durable transaction owner merely for convenience.

Location import is a longer workflow:

`upload -> LocationImport + staged file -> Quartz projection/job -> LocationImportService -> streaming parser -> relational batches/progress -> optional enrichment handoff -> SSE reload hint`

Location import performs no provider credential resolution, provider admission,
reverse-geocoding HTTP, inline enrichment, or per-record enrichment delay. Address
enrichment is a separate opted-in workflow after committed import state.

`Parsers/ILocationDataParser` streams `Location` records; `LocationDataParserFactory` selects the current Google Timeline, Wayfarer GeoJSON, GPX, KML or CSV parser. `Services/LocationImports/` owns lifecycle/projection concerns around the job rather than format parsing itself.

Trip PDF and map capture cross a browser boundary. `TripExportService`/PDF helpers render the application view, while `BrowserWorkflow`, `MapSnapshotService` and thumbnail capture use Playwright/Chromium as bounded infrastructure. Treat browser launch/capture as an external runtime dependency, not as a reason to move trip domain behavior into browser code.

## Quartz jobs and workflows

Quartz uses a PostgreSQL ADO job store with `qrtz_` tables. `Jobs/` contains executable `IJob` entry points; `ScopedJobFactory` resolves each execution from DI. Maintenance jobs are scheduled centrally in startup, while workflows such as location import and enrichment have service-owned lifecycle/reconciliation code plus Quartz projection.

A job is a background execution adapter, not automatically the owner of durable workflow semantics. Put idempotency, epoch/state transitions, transactions and recovery rules in the corresponding service/workflow owner so they can be tested below Quartz.

## SSE ownership

`SseService` owns bounded best-effort transport, admission, channel lifetime, heartbeats and bounded fan-out. Durable mutations remain authoritative even if an SSE delivery fails.

There are two important stream families:

- `GET /api/sse/stream/location-update/{username}` is the anonymous public live Timeline stream. The controller admits only an effectively public, live Timeline and rechecks eligibility during delivery.
- Dedicated protected streams such as `/api/sse/import`, `/api/sse/group-notifications`, `/api/sse/group/{groupId}` and `/api/mobile/sse/...` bind delivery to the authenticated user/group and their current authorization.

Import, enrichment, invitation and membership streams use content-free reload hints where the client should fetch current durable state. Group/location streams carry typed event payloads. Do not add a new generic public stream when the data belongs to a protected resource.

## Frontend ownership

Razor pages and views use Bootstrap and area/view-aligned JavaScript. `frontend.config.yaml` maps view keys to `wwwroot/js/...`; MvcFrontendKit bundles those assets for production.

The Trip Editor is intentionally different: Vue owns editor interaction/state under `ClientApps/trip-editor/src`, calls the Trip Editor API, and Vite produces the manifest/assets consumed by ASP.NET Core. Keep Trip Editor state transitions in the Vue client/API contract rather than duplicating them in unrelated Razor scripts.

## Where does this change belong?

| Change | Primary owner | Common adjacent boundaries |
| --- | --- | --- |
| MVC page or form | Matching `Areas/*/Controllers`, view and area-aligned JS | authorization, antiforgery, service tests |
| JSON/mobile endpoint | `Areas/Api/Controllers/` | token/cookie auth, ownership, API errors |
| Trip Editor behavior | `ClientApps/trip-editor/` plus `TripEditor*Controller`/services | antiforgery, concurrency, Vite build |
| Durable entity/field | `Models/`, model configuration, `Migrations/` | PostgreSQL/PostGIS, transaction tests |
| Background workflow | owning service namespace plus `Jobs/` adapter | Quartz projection, cancellation, recovery |
| Location import format | `Parsers/` and `LocationDataParserFactory` | streaming, staged files, relational dedupe |
| External provider | `Services/LocationProviders/`, `ExternalRouting/` or `LocationEnrichment/` | protected credentials, contact admission, bounded HTTP |
| Files/cache/logs | existing storage service plus `StoragePaths` | durability class, permissions, cleanup |
| Live update | durable owner plus `SseService` channel | authorization/reload hint, best-effort delivery |

See [Extending Wayfarer](extending.md) for concrete change recipes.
