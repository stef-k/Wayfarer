# Wayfarer

[![Tests](https://github.com/stef-k/Wayfarer/actions/workflows/tests.yml/badge.svg)](https://github.com/stef-k/Wayfarer/actions/workflows/tests.yml)

Wayfarer is a self-hosted travel companion that lets you keep a private location timeline, plan trips, and optionally share real-time progress with trusted people. The web app runs on ASP.NET Core and PostgreSQL/PostGIS, and a companion mobile app (WayfarerMobile) can stream live GPS updates or manual check-ins straight to your server.

## Guided self-hosting

The recommended production path is one small bootstrap and one guided Docker/Compose
setup command. On a supported Linux AMD64 host with Docker Engine, Compose v2 and
sudo/root access, obtain `wayfarerctl-linux-amd64.tar.gz` from the official stable
[GitHub Release](https://github.com/stef-k/Wayfarer/releases), verify its Release asset
SHA-256, then extract it in a trusted directory:

```sh
tar -xzf wayfarerctl-linux-amd64.tar.gz
chmod +x wayfarerctl
sudo ./wayfarerctl setup
```

Setup discovers, acquires and verifies the matching stable deployment bundle and exact
images, then guides you through hostname, proxy and administrator-password choices.
You do not need a source clone, .NET/Node/PostgreSQL/Nginx/Certbot installation, a
manually selected deployment bundle or copied image digests on this path.
See [Install & Self-Hosting](docs/02-Install-and-Dependencies.md) and the
[operator guide](docs/29-Wayfarerctl.md) for prerequisites, exact-version/offline setup,
retained release dispatch, recovery and update planning.

**Release availability:** the lean install implementation is awaiting its first genuine
Compose stable release. v1.9.19 and earlier are source-only. Public installation
acceptance remains open under #713 and requires #715's ARM64 expansion before the
first Compose stable is published. The commands above apply to that future release;
they do not claim these public assets already exist.

## Screenshots

[![Public timeline](docs/images/public-timeline.JPG)](docs/images/public-timeline.JPG)

[![Segment editing](docs/images/segment-edit-2.JPG)](docs/images/segment-edit-2.JPG)

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
* **Import history** from Google Timeline JSON, Wayfarer GeoJSON, CSV, GPX, and location-history KML. Generic GeoJSON is not accepted as a new history upload.
* **Import deduplication** prevents duplicate entries automatically.
* **Resumable missing-address enrichment** is an explicit, user-controlled Quartz workflow with state- and authority-specific Start, Pause, Resume, Cancel, and Retry deferred controls. Location import commits independently and performs no inline provider work; committed blank rows feed the separate opted-in workflow while imported/manual data remains preserved.
* **Metadata preservation** — accuracy, speed, altitude, heading tracked per location.
* **Export locations** to GeoJSON, KML, CSV, or GPX formats with full metadata.
* **Personal location providers** support storage-authorized Geoapify geocoding/routing and Mapbox Permanent Geocoding through protected credentials, independent capability verification/selection, and provider-native guards; capture continues when enrichment is paused. See the [personal provider guide](docs/24-Personal-Location-Providers.md).
* **Wikipedia integration** — discover related articles for any location or trip place.
* **Location statistics** — visit counts by country, region, and city.
* **Bulk edit notes** to update multiple records at once.
* **Inline activity editing** — change activity type directly from location views.

### Trip Planning

- Organize trips into **Regions**, **Places**, **Areas**, and **Segments**.
* Add **notes** (rich HTML), **colors**, **icons**, and **travel modes**.
* **Trip tags** for organization with public browsing by tag.
* **Cover images** and auto-generated **thumbnails** for trip cards.
* **Import trips** from Google MyMaps KML or Wayfarer format.
* **Export trips** to PDF (printable guide with maps and clickable links) or KML.

### Automatic Visit Detection

- Detects when GPS pings arrive near planned trip places.
* **Two-hit confirmation** reduces false positives from GPS noise.
* Records **visit events** with arrival/departure times and place snapshots.
* **Visit backfill** — analyze existing location history to create visits retroactively.
* Works with all location sources: mobile tracking, check-ins, API entries.
* Configurable detection radius, accuracy thresholds, and confirmation requirements.

### Groups & Real-Time Sharing

- Create **groups** for family, friends, or teams.
* **Roles**: Owner, Manager, Member with different permissions.
* **Invitation system** with token-based acceptance.
* **Real-time location sharing** via Server-Sent Events (SSE).
* **Visit notifications** when group members arrive at planned places.

### Privacy Controls

- **Hidden Areas** — polygon exclusion zones; locations inside never appear publicly.
* **Public timeline threshold** — hide most recent hours/days.
* **Public/private toggle** — timeline and trips are private by default.
* **Embeddable timeline** — iframe your public timeline into other websites.

### Admin Features

- **User management** — create, edit, lock/unlock, assign roles.
* **Application settings** — location thresholds, visit detection, upload limits.
* **Tile provider settings** — presets, custom templates, API key support.
* **GPS accuracy filtering** — reject low-quality location readings.
* **Background jobs** — pause, resume, cancel running jobs; view history and status.
* **Cache management** — tile cache statistics and LRU cleanup.
* **Audit logs** — track all admin actions for compliance.
* **Log viewer** — real-time application log viewing with search.

## Development (source checkout)

```bash
dotnet restore
dotnet run --no-launch-profile -- database migrate
dotnet run                  # launch locally (reads appsettings.Development.json)
```

Useful app CLI commands:

```bash
dotnet run --no-launch-profile -- help
dotnet run --no-launch-profile -- version
```

For deployment/admin CLI details, including password reset, see the Deployment Guide.

For Vue Trip Editor development, run the ASP.NET Core app and the Vite
dev server side-by-side:

```bash
npm run dev
```

Trip Editor browser verification is dev-only tooling. After the ASP.NET Core app
and Vite are already running, configure local credentials with `WAYFARER_E2E_*`
environment variables or ignored `.local/manual-verification.md`, then run:

```bash
npm run test:e2e:trip-editor
```

See `docs/22-Testing.md` for the required variables, local file format, and
browser install command. Do not reset passwords or create users for this harness
unless that is explicitly approved.

Production-like bundle acceptance should restore repository-local .NET tools,
then build frontend prerequisites with `dotnet tool restore`,
`dotnet frontend build`, `npm ci`, and `npm run build` before `dotnet publish`.
Run the published output rather than source-tree
`ASPNETCORE_ENVIRONMENT=Production dotnet run`; published output is the supported
layout for scoped CSS, MvcFrontendKit bundles, and Trip Editor Vite assets.
The Trip Editor publish output must include
`wwwroot/vite/trip-editor/manifest.json` and the CSS/JS files referenced by that
manifest.

For source development, see [development setup](docs/14-Setup.md). Production web
startup does not migrate, seed or create a default administrator. Native/manual
installations require the [explicit maintenance sequence](docs/20-Deployment.md#post-installation).
After guided Compose setup, sign in as `admin` with the password you chose, configure
**Admin > Settings**, and keep public registration disabled unless deliberately enabled.

## Documentation

Full documentation available via GitHub Pages:

* **User Guide**: [stef-k.github.io/Wayfarer](https://stef-k.github.io/Wayfarer/#/user/0-Index)
* **Developer Guide**: [stef-k.github.io/Wayfarer](https://stef-k.github.io/Wayfarer/#/developer/0-Index)
* Local browsing: `docsify serve docs`

## Mobile Companion

The [WayfarerMobile](https://github.com/stef-k/WayfarerMobile) app (built with .NET MAUI) connects to your server:

* **Live GPS tracking** with configurable intervals
* **Manual check-ins** for specific locations
* **SSE subscriptions** for real-time group updates
* **QR code pairing** for easy server connection

## Technology Stack

| Layer | Technology |
|-------|------------|
| Backend | ASP.NET Core 10 MVC, Quartz.NET |
| Database | PostgreSQL/PostGIS via EF Core & NetTopologySuite |
| Spatial | GiST indexes, ST_DWithin queries |
| Frontend | Razor views, Leaflet maps, vanilla JS; Vue for the Trip Editor |
| Bundling | [MvcFrontendKit](https://github.com/nickofc/MvcFrontendKit) (esbuild) plus Vite for Trip Editor assets |
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

* **Trips** — list, retrieve, tags, boundaries
* **Locations** — GPS logging, check-ins, statistics
* **Visits** — visit history, CRUD operations
* **Groups** — membership, invitations
* **SSE Streams** — real-time updates for locations, visits, jobs, invitations

See [API documentation](https://stef-k.github.io/Wayfarer/#/developer/23-API) for full endpoint reference.

## Background Jobs

Quartz.NET scheduler with persistent job store:

* **LocationImportJob** — process uploaded location files with SSE progress
* **VisitCleanupJob** — close stale visits, remove unconfirmed candidates
* **AuditLogCleanupJob** — remove logs older than 2 years
* **LogCleanupJob** — prune application logs older than 1 month

All jobs support cancellation and report status via SSE to the admin panel.

## Issues, Ideas & PRs

This is a spare-time project that currently meets my needs. I'll improve it when I can, but **there's no guaranteed schedule or roadmap**.

* **Issues & feature requests**: Please open them—I'll read when I can.
* **Pull requests**: welcomed. Reviews and merges may be delayed.
* To improve your chances:
  * Keep PRs small and focused.
  * Explain the motivation and user impact.
  * Include repro steps, tests (if applicable), and docs updates.

> Note: This project is MIT-licensed and provided **"as is" without warranty**.

## Branding

The **Wayfarer** name and project identity are intended to refer to this repository.
Forks and modified redistributions should **use a different name** to avoid confusion and false association.

## License

Wayfarer is released under the terms of [LICENSE.txt](LICENSE.txt). Contributions are welcome—please include tests and documentation updates where it makes sense. Happy travels!
