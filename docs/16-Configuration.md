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

Tile Provider Settings (Admin UI)
- **Tile Provider** — select from presets (OpenStreetMap, Carto Light/Dark, ESRI Satellite) or configure a custom URL template.
- **Custom URL Template** — use `{z}`, `{x}`, `{y}` placeholders; optionally `{apikey}` for providers requiring authentication.
- **API Key** — stored securely for tile providers that require it (e.g., Mapbox, Thunderforest).
- **Attribution** — HTML attribution text displayed on maps; auto-filled for presets.
- Provider changes trigger automatic cache purge to avoid tile mixing.

Location Thresholds (Admin UI)
- **Distance Threshold** — minimum distance (meters) before logging a new location.
- **Time Threshold** — minimum time (seconds) between location logs.
- **GPS Accuracy Threshold** — maximum acceptable accuracy value (default 50m); readings with higher values are rejected.

Visit Detection (Admin UI)
- **Detection Radius** — distance (meters) from place center to trigger visit detection.
- **Notification Cooldown** — minimum delay (seconds) between visit notifications for the same place, reducing SSE spam.
- **End-Visit Timeout** — time without pings before a visit is considered ended.

Tile Rate Limiting
- Anonymous tile requests are rate-limited (default: 500 requests/minute per IP).
- Configurable via `TileRateLimitPerMinute` setting.
- X-Forwarded-For header trusted from localhost/private IPs for proper client identification behind reverse proxies.

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
