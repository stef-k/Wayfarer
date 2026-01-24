# Architecture

Overview of Wayfarer's technical architecture, design patterns, and application structure.

---

## Technology Stack

| Layer | Technology |
|-------|------------|
| Backend | ASP.NET Core 10 MVC |
| Database | PostgreSQL + PostGIS via EF Core |
| Spatial | NetTopologySuite, GiST indexes |
| Scheduler | Quartz.NET (ADO.NET job store) |
| Logging | Serilog (console, file, PostgreSQL) |
| Frontend | Razor views, Leaflet, vanilla JS |
| Bundling | [MvcFrontendKit](https://github.com/nickofc/MvcFrontendKit) (esbuild-based) |
| Map Icons | [wayfarer-map-icons](https://github.com/stef-k/wayfarer-map-icons) |
| Real-time | Server-Sent Events (SSE) |
| PDF Export | Microsoft Playwright |
| Geocoding | Mapbox API (optional) |
| Auth | ASP.NET Core Identity with 2FA |
| Testing | xUnit |

---

## Application Areas

The application uses ASP.NET Areas for logical separation:

| Area | Purpose |
|------|---------|
| **Admin** | System-wide administration (users, settings, jobs, logs, audit logs, activity types, API tokens) |
| **Api** | RESTful JSON APIs for trips, locations, groups, invitations, SSE, icons, settings |
| **Identity** | Authentication UI for login/registration and account management |
| **Manager** | Business-level administration (groups, users, API tokens with delegated scope) |
| **User** | User features (trips, locations, visits, groups, imports, settings) |
| **Public** | Public-facing pages (shared timelines, public trips, trip discovery) |

---

## Routing

- MVC controllers under `Controllers/` and per-area controllers follow conventional routes.
- API controllers use `[Area("Api")]` and `[Route("api/[controller]")]`.
- Trip exports: `TripExportController` uses routes like `Trip/ExportPdf/{id}`.

### Public Timeline Routes

- User public timeline: `GET /Public/Users/Timeline/{username}` (full page view)
- Embeddable view: `GET /Public/Users/Timeline/{username}/embed`
- Data API: `POST /Public/Users/GetPublicTimeline` (with viewport/zoom filter), `GET /Public/Users/GetPublicStats/{username}`
- Hidden Areas are enforced: locations inside user-defined hidden polygons are excluded from public results.

### Base Controllers

- `Controllers/BaseController` — shared helpers for MVC controllers (logging, alerts, titles).
- `Areas/Api/Controllers/BaseApiController` — token extraction, role helpers, numeric/geometry sanitizers.

---

## High-Level Components

- **Controllers** — HTTP endpoints for MVC and API
- **Services** — Application logic (imports, exports, geocoding, tiles, SSE)
- **Parsers** — File format ingestion (JSON, GPX, KML, GeoJSON)
- **Jobs** — Background processing via Quartz
- **Models** — Domain entities, DTOs, options
- **Middleware** — Performance logging, dynamic request size
- **Util** — Helpers for roles, time, distance, HTML

---

## Data Flow Examples

### Location Import

```
Upload → LocationImport row → Quartz job → LocationImportService
       → Parse batches → Optional reverse geocoding → DB insert
       → SSE progress updates
```

### Trip Export

```
Controller → ITripExportService → KML or PDF stream → HTTP response
PDF: Playwright renders Razor view → Screenshots → PDF document
```

### Mobile SSE

```
SseService channels → Broadcast status updates → Connected clients
Channels: locations, groups, visits, jobs, invitations, memberships
```

### Visit Detection

```
Location ping → PlaceVisitDetectionService → PostGIS ST_DWithin query
             → Two-hit confirmation → PlaceVisitEvent → SSE notification
```

### Visit Backfill

```
User request → VisitBackfillService → Chunked spatial queries (if >10k places)
            → Tier-based analysis (1×, 2×, configurable multiplier)
            → Confirmed visits + Consider Also suggestions
            → Place context map for review → Batch create/delete
```

---

## Role Model

Three application roles with hierarchical permissions:

| Role | Access Level |
|------|--------------|
| **Admin** | Full system access, user management, settings |
| **Manager** | Group management, member oversight |
| **User** | Personal data, trips, locations, group participation |

Role constants defined in `Util/ApplicationRoles.cs`.

---

## Database Design

### Spatial Features

- PostGIS extension for geographic data
- `Point` geometry for locations and places
- `Polygon` geometry for areas and hidden zones
- `LineString` geometry for segments
- **GiST indexes** for efficient spatial queries

### Key Tables

| Table | Purpose |
|-------|---------|
| `Locations` | User location timeline entries |
| `Trips`, `Regions`, `Places`, `Areas`, `Segments` | Trip structure |
| `PlaceVisitEvents` | Automatic visit records |
| `Groups`, `GroupMembers`, `Invitations` | Group system |
| `TileCacheEntries` | Map tile metadata |
| `qrtz_*` | Quartz scheduler persistence |
| `AuditLogs` | Admin action tracking |

---

## Real-Time Architecture

### Server-Sent Events (SSE)

- `SseService` manages client connections
- Multiple channels for different event types
- Automatic reconnection support
- Used for: location updates, import progress, job status, visits, invitations

### SSE Channels

```
/api/sse/stream/location-update/{userName}
/api/sse/stream/group-location-update/{groupId}
/api/sse/stream/visits
/api/sse/stream/job-status
/api/sse/stream/import-progress
/api/sse/stream/invitations
/api/sse/stream/memberships
```

---

## Background Jobs

Quartz.NET with persistent ADO.NET job store:

- **LocationImportJob** — Process uploaded location files
- **VisitCleanupJob** — Close stale visits, remove candidates
- **AuditLogCleanupJob** — Prune old audit entries
- **LogCleanupJob** — Remove old log files

Jobs support cancellation via `CancellationToken` and report status via SSE.

---

## Map Tile Caching

Two-tier caching strategy:

| Zoom Level | Strategy |
|------------|----------|
| 0–8 | Permanent cache (~1.3–1.75 GB) |
| 9+ | LRU eviction (configurable limit) |

- `TileCacheService` manages cache lifecycle
- Respects OpenStreetMap fair-use policies
- Admin controls for cache statistics and cleanup

---

## Frontend Architecture

### Bundling

[MvcFrontendKit](https://github.com/nickofc/MvcFrontendKit) handles JavaScript and CSS bundling:

- **Convention-based loading** — JS files auto-linked by view path (e.g., `Areas/User/Trips/Index` → `wwwroot/js/Areas/User/Trips/Index.js`)
- **esbuild-powered** — fast bundling with ES2020 target and sourcemaps
- **Global + per-view bundles** — `site.js` and `site.css` load globally; view-specific scripts load automatically
- **Development mode** — unbundled modules for debugging
- **Production mode** — minified bundles in `/dist`
- **Configuration**: `frontend.config.yaml`

### State Management

Trip editing uses a reactive store pattern inspired by [simple-reactive-store](https://github.com/stef-k/simple-reactive-store):

- **Immutable snapshots** via `getState()`
- **Fine-grained subscriptions** via `subscribe()` / `subscribeOnce()`
- **Single-pass dispatch** — mutate state → broadcast action
- **Key files**: `wwwroot/js/Areas/User/Trip/store.js`, `storeInstance.js`

### Map Icons

[wayfarer-map-icons](https://github.com/stef-k/wayfarer-map-icons) provides consistent marker icons:

- **Location**: `wwwroot/icons/wayfarer-map-icons/`
- **Formats**: PNG markers and circles in multiple colors
- **CSS**: Icon stylesheet for consistent styling
- **API**: `/api/icons` endpoints for icon discovery

---

## Security Architecture

- ASP.NET Core Identity with optional 2FA
- API token authentication for mobile/external access
- Role-based access control (RBAC)
- Hidden areas for location privacy
- Public/private visibility controls

