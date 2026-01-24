# Wayfarer

[![Tests](https://github.com/stef-k/Wayfarer/actions/workflows/tests.yml/badge.svg)](https://github.com/stef-k/Wayfarer/actions/workflows/tests.yml)

Wayfarer is a self-hosted travel companion that lets you keep a private location timeline, plan trips, and optionally share real-time progress with trusted people. The web app runs on ASP.NET Core and PostgreSQL/PostGIS, and a companion mobile app (WayfarerMobile) can stream live GPS updates or manual check-ins straight to your server.

## Security Model & Intended Use

Wayfarer is a privacy-first, self-hosted location timeline and trip companion for individuals, families, and teams who want to keep their data on their own infrastructure.
By default, deployments should assume a **closed registration model** and operate behind a reverse proxy with HTTPS.

If you expose Wayfarer publicly, you are responsible for:

* keeping the server updated
* enforcing strong admin credentials and 2FA
* rate limiting API endpoints (especially location logging)
* avoiding public access to admin-only routes and logs

## Key Features

### Location Timeline
- **Record locations** via mobile app GPS, manual check-ins, or API.
- **Import history** from Google Timeline (JSON), GPX, KML, GeoJSON, and CSV.
- **Import deduplication** prevents duplicate entries automatically.
- **Metadata preservation** — accuracy, speed, altitude, heading tracked per location.
- **Export locations** to GeoJSON, KML, CSV, or GPX formats with full metadata.
- **Reverse geocoding** enriches coordinates with addresses (Mapbox token required).
- **Wikipedia integration** — discover related articles for any location or trip place.
- **Location statistics** — visit counts by country, region, and city.
- **Bulk edit notes** to update multiple records at once.
- **Inline activity editing** — change activity type directly from location views.

### Trip Planning
- Organize trips into **Regions**, **Places**, **Areas**, and **Segments**.
- Add **notes** (rich HTML), **colors**, **icons**, and **travel modes**.
- **Trip tags** for organization with public browsing by tag.
- **Cover images** and auto-generated **thumbnails** for trip cards.
- **Import trips** from Google MyMaps KML or Wayfarer format.
- **Export trips** to PDF (printable guide with maps and clickable links) or KML.

### Automatic Visit Detection
- Detects when GPS pings arrive near planned trip places.
- **Two-hit confirmation** reduces false positives from GPS noise.
- Records **visit events** with arrival/departure times and place snapshots.
- **Visit backfill** — analyze existing location history to create visits retroactively.
- Works with all location sources: mobile tracking, check-ins, API entries.
- Configurable detection radius, accuracy thresholds, and confirmation requirements.

### Groups & Real-Time Sharing
- Create **groups** for family, friends, or teams.
- **Roles**: Owner, Manager, Member with different permissions.
- **Invitation system** with token-based acceptance.
- **Real-time location sharing** via Server-Sent Events (SSE).
- **Visit notifications** when group members arrive at planned places.

### Privacy Controls
- **Hidden Areas** — polygon exclusion zones; locations inside never appear publicly.
- **Public timeline threshold** — hide most recent hours/days.
- **Public/private toggle** — timeline and trips are private by default.
- **Embeddable timeline** — iframe your public timeline into other websites.

### Admin Features
- **User management** — create, edit, lock/unlock, assign roles.
- **Application settings** — location thresholds, visit detection, upload limits.
- **Tile provider settings** — presets, custom templates, API key support.
- **GPS accuracy filtering** — reject low-quality location readings.
- **Background jobs** — pause, resume, cancel running jobs; view history and status.
- **Cache management** — tile cache statistics, LRU cleanup, MBTiles for mobile.
- **Audit logs** — track all admin actions for compliance.
- **Log viewer** — real-time application log viewing with search.

## Get Started

```bash
dotnet restore
dotnet ef database update   # apply Postgres/PostGIS migrations
dotnet run                  # launch locally (reads appsettings.Development.json)
```

1. Sign in with the seeded `admin` / `Admin1!` account and change the password.
2. Configure thresholds, cache limits, and registration mode under **Admin > Settings**.
3. Invite users or enable open registration; managers only see data from users who trust them.
4. (Optional) Add a personal Mapbox token on your account to enrich locations with addresses.

> **Note:** The `appsettings.json` files contain placeholder database passwords. For production, configure credentials via systemd environment variables—see the [Deployment Guide](https://stef-k.github.io/Wayfarer/#/developer/26-Deployment).

### Production Notes

For real deployments, it's recommended to:

* keep public registration disabled unless you explicitly want open signups
* run behind a reverse proxy (Nginx / Caddy) with HTTPS
* store secrets (DB credentials, tokens) via environment variables (systemd), not in `appsettings.json`
* enable request limiting for API endpoints that accept GPS updates

## Documentation

Full documentation available via GitHub Pages:

- **User Guide**: [stef-k.github.io/Wayfarer](https://stef-k.github.io/Wayfarer/#/user/0-Index)
- **Developer Guide**: [stef-k.github.io/Wayfarer](https://stef-k.github.io/Wayfarer/#/developer/0-Index)
- Local browsing: `docsify serve docs`

## Mobile Companion

The [WayfarerMobile](https://github.com/stef-k/WayfarerMobile) app (built with .NET MAUI) connects to your server:

- **Live GPS tracking** with configurable intervals
- **Manual check-ins** for specific locations
- **Offline map tiles** cached for use without connectivity
- **SSE subscriptions** for real-time group updates
- **QR code pairing** for easy server connection

## Technology Stack

| Layer | Technology |
|-------|------------|
| Backend | ASP.NET Core 10 MVC, Quartz.NET |
| Database | PostgreSQL/PostGIS via EF Core & NetTopologySuite |
| Spatial | GiST indexes, ST_DWithin queries |
| Frontend | Razor views, Leaflet maps, vanilla JS |
| Bundling | [MvcFrontendKit](https://github.com/nickofc/MvcFrontendKit) (esbuild) |
| Map Icons | [wayfarer-map-icons](https://github.com/stef-k/wayfarer-map-icons) |
| Real-time | Server-Sent Events (SSE) |
| PDF Export | Microsoft Playwright |
| Logging | Serilog (console, file, DB) |
| Auth | ASP.NET Core Identity with 2FA |
| Tests | xUnit |

### Map Tiles & Attribution

Wayfarer supports multiple tile providers via Admin settings (OpenStreetMap, Carto, ESRI, or custom URL templates).
The app includes **built-in tile caching** to reduce bandwidth and respect fair-use policies.
If you use public OpenStreetMap tile servers, ensure proper attribution is displayed.
For heavy usage, consider a dedicated tile provider or self-hosted tile server.

## API

RESTful API with Bearer token authentication:

- **Trips** — list, retrieve, tags, boundaries
- **Locations** — GPS logging, check-ins, statistics
- **Visits** — visit history, CRUD operations
- **Groups** — membership, invitations
- **SSE Streams** — real-time updates for locations, visits, jobs, invitations

See [API documentation](https://stef-k.github.io/Wayfarer/#/developer/23-API) for full endpoint reference.

## Background Jobs

Quartz.NET scheduler with persistent job store:

- **LocationImportJob** — process uploaded location files with SSE progress
- **VisitCleanupJob** — close stale visits, remove unconfirmed candidates
- **AuditLogCleanupJob** — remove logs older than 2 years
- **LogCleanupJob** — prune application logs older than 1 month

All jobs support cancellation and report status via SSE to the admin panel.

## Issues, Ideas & PRs

This is a spare-time project that currently meets my needs. I'll improve it when I can, but **there's no guaranteed schedule or roadmap**.

- **Issues & feature requests**: Please open them—I'll read when I can.
- **Pull requests**: welcomed. Reviews and merges may be delayed.
- To improve your chances:
  - Keep PRs small and focused.
  - Explain the motivation and user impact.
  - Include repro steps, tests (if applicable), and docs updates.

> Note: This project is MIT-licensed and provided **"as is" without warranty**.

## Branding

The **Wayfarer** name and project identity are intended to refer to this repository.
Forks and modified redistributions should **use a different name** to avoid confusion and false association.

## License

Wayfarer is released under the terms of [LICENSE.txt](LICENSE.txt). Contributions are welcome—please include tests and documentation updates where it makes sense. Happy travels!
