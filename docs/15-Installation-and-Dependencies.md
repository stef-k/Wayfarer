# Installation & Dependencies

Supported Platforms

- Windows 10/11, Linux distributions, macOS (dev), Docker (if you build your own image)

Core Dependencies

- .NET 9 SDK
- PostgreSQL (13+) with PostGIS extension
- Reverse proxy (nginx, Caddy, IIS) recommended

Database Preparation

- Create DB: CREATE DATABASE wayfarer;
- Enable PostGIS in the DB: CREATE EXTENSION postgis;
- Enable PostGIS in the DB: CREATE EXTENSION citext;
- Ensure the connecting DB user has privileges to create tables and indices.

Runtime Artifacts

- Quartz creates `qrtz_*` tables on startup via `QuartzSchemaInstaller`.
- EF Core creates/updates schema as configured by the model.

Build & Run

- Restore: `dotnet restore`
- Build: `dotnet build`
- Run (dev): `dotnet run` (uses `appsettings.Development.json`)
- Watch: `dotnet watch run`

Uploads & Exports

- Uploads: temp upload staging under the app’s `Uploads/Temp/` folder by default (visible in Admin Settings page with size totals).
- Exports: use `Exports/` folder for generated artifacts when applicable.

Tile Cache

- `CacheSettings:TileCacheDirectory` controls where map tiles are persisted.

Reverse Proxy

- Forward `X-Forwarded-For`, `X-Forwarded-Proto`, `X-Forwarded-Host`.
- Configure trusted proxies and networks in `ConfigureForwardedHeaders` (see `Program.cs`).

Recommended Packages (Linux)

- `postgresql postgresql-contrib postgis`
- `nginx` or `caddy`
- Ensure `libicu` as required by .NET globalization on minimal distros.
