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

**Core Settings:**
- **Required Hits** — number of GPS pings needed to confirm a visit (2–5, default 2).
- **Min Radius** — minimum detection radius in meters (10–200m).
- **Max Radius** — maximum detection radius in meters (50–500m).
- **Accuracy Multiplier** — scales detection radius based on GPS accuracy (0.5–5.0×).
- **Accuracy Reject** — reject locations with accuracy worse than this value (0–1000m).
- **Max Search Radius** — maximum search distance for nearby places (50–2000m).

**Timing Settings:**
- **Notification Cooldown** — minimum delay between visit notifications for same place (-1 to disable, up to 720 hours).
- **Notes Snapshot Max Chars** — maximum HTML characters preserved in visit snapshot (1000–200000).

**Derived Settings (auto-calculated from Time Threshold):**
- **Hit Window** — time window for confirming hits.
- **Candidate Stale** — time before unconfirmed candidates are cleaned up.
- **Visit End After** — timeout before a visit is considered ended.

**Backfill Suggestions:**
- **Suggestion Radius Multiplier** — outer search radius for "Consider Also" suggestions (2–100×, default 50×).
- **Derived Tiers** — admin panel shows 3 tiers with calculated radii and hit requirements.

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
