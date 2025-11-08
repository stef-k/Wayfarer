# Wayfarer

Wayfarer is a self-hosted travel companion that lets you keep a private location timeline, plan trips, and optionally share real-time progress with trusted people. The web app runs on ASP.NET Core and PostgreSQL/PostGIS, and a companion mobile app (Wayfarer.Mobile) can stream live GPS updates or manual check‑ins straight to your server.

## What you get

- **Your own location journal** – record live points, import historical data, and publish a public view with hidden-area privacy controls if you choose.
- **Trip planning tools** – organise places, areas, and routes, add notes and colours, and export itineraries to PDF or KML.
- **Trusted sharing** – managers you approve can follow your latest positions; shared dashboards update instantly via server-sent events.
- **Offline-friendly maps** – tile caching keeps base maps fast and respects tile provider limits; higher zoom levels are pruned automatically.
- **Admin controls** – manage users, thresholds, caches, jobs, audit logs, and reverse-geocoding tokens from a dedicated admin area.

## Get started in minutes

```bash
dotnet restore
dotnet ef database update   # apply Postgres/PostGIS migrations
dotnet run                  # launch locally (reads appsettings.Development.json)
```

1. Sign in with the seeded `admin` / `Admin1!` account and change the password.
2. Configure thresholds, cache limits, and registration mode under **Admin > Settings**.
3. Invite users or enable open registration; managers only see data from users who trust them.
4. (Optional) Add a personal Mapbox token on your account to enrich locations with addresses.

Need more detail? See the GitHub Pages docs:

- **User guide:** [stef-k.github.io/Wayfarer](https://stef-k.github.io/Wayfarer/#/user/0-Index)
- **Developer guide:** [stef-k.github.io/Wayfarer](https://stef-k.github.io/Wayfarer/#/developer/0-Index)
- Prefer local browsing? Run `docsify serve docs` inside the repo.

## Mobile companion

The [Wayfarer.Mobile](https://github.com/stef-k/Wayfarer.Mobile) app (built with .NET MAUI) connects to your server, handles live tracking and manual check-ins, listens to SSE updates, and caches trip tiles for offline use. Users simply scan a QR code or paste their server URL and API token.

## Technology snapshot

- Backend: ASP.NET Core 9 MVC + Quartz background jobs
- Database: PostgreSQL/PostGIS via EF Core & NetTopologySuite
- Frontend: Razor views, Leaflet maps, vanilla JS, SSE
- Logging & audit: Serilog sinks (console/file/DB) plus `AuditLogs` table
- Tests: xUnit project at `tests/Wayfarer.Tests`

## License

Wayfarer is released under the terms of [LICENSE.txt](LICENSE.txt). Contributions are welcome—please include tests and documentation updates where it makes sense. For issues or ideas, open a GitHub issue or PR. Happy travels!

