# Configuration

Files
- `appsettings.json` — base config
- `appsettings.Development.json` — local overrides
- Environment variables or user‑secrets — recommended for sensitive values

ConnectionStrings
- `DefaultConnection` — PostgreSQL connection string with PostGIS‑enabled database.
- The `appsettings.json` files contain **placeholder passwords** (`CHANGE_ME_BEFORE_DEPLOY`).
- **Production:** Configure via systemd environment variable (overrides JSON):
  ```ini
  # In /etc/systemd/system/wayfarer.service under [Service]:
  Environment="ConnectionStrings__DefaultConnection=Host=localhost;Database=wayfarer;Username=user;Password=SECRET"
  ```
- **Development:** Use `dotnet user-secrets` or edit `appsettings.Development.json` locally.
- The `install.sh` deployment script configures this automatically.

Logging
- `Logging:LogLevel:*` — log verbosity per category.
- `Logging:LogFilePath:Default` — path to rolling log file (ensure directory exists).
- Serilog sinks: console, file, and PostgreSQL (table `AuditLogs`).

CacheSettings
- `CacheSettings:TileCacheDirectory` — local directory for map tile cache.

Uploads
- Upload staging directory defaults under `Uploads/Temp/` (path visible in Admin Settings). Ensure writable by the app.

Reverse Geocoding (Per‑User)
- Users can store a personal Mapbox API token as an `ApiToken` named "Mapbox"; when present, imports and manual adds enrich addresses.

Mobile
- `MobileGroups:Query:DefaultPageSize` and `MaxPageSize` — paging for mobile group queries.
- `MobileSse:HeartbeatIntervalMilliseconds` — SSE keepalive interval.

Reverse Proxy
- Forwarded headers set in `Program.cs` for nginx or similar. Adjust trusted proxies/networks per environment.

Upload Size
- Effective upload size is enforced via `ApplicationSettings.UploadSizeLimitMB` in DB and `DynamicRequestSizeMiddleware`.

Secrets
- Keep tokens, API keys, and passwords out of `appsettings*.json` in production. Use environment variables or secret stores.
