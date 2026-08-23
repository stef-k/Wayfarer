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

Application
- `Application:ContactEmail` — contact email included in the User-Agent header sent to tile providers (e.g. OpenStreetMap). OSM's tile usage policy requires an honest User-Agent identifying the application. Set this to a monitored email address. Default: `noreply@wayfarer.app`. In production, configure via systemd environment variable: `Application__ContactEmail=admin@your-domain.example`.
- `AllowedHosts` — semicolon-separated exact public DNS hostnames allowed to address Wayfarer and supply its origin-only tile-provider Referer. Use `AllowedHosts=wayfarer.example.com` for one hostname or `AllowedHosts=wayfarer.example.com;www.wayfarer.example.com` for several. Do not include wildcards, IP literals, localhost/private names, ports, or URL schemes. Ports received separately through trusted forwarded headers are preserved after hostname authorization.

CacheSettings
- `CacheSettings:TileCacheDirectory` — local directory for map tile cache.
- **Max Tile Cache Size** (Admin UI) — controls the LRU cache size for zoom >= 9 tiles. Default: 1024 MB. Minimum: 256 MB (OSM requires tiles cached for at least 7 days). Set to `-1` to disable the size limit (no LRU eviction). Zoom 0-8 tiles (~1 GB) are cached permanently and do not count against this limit.

Image Proxy Resource Limits
- **Max Proxy Image Download Size** (Admin UI) is the encoded origin-response limit. Its existing range remains 5–200 MiB and its default is 50 MiB.
- Optimization intentionally supports still/single-frame JPEG, PNG, WebP, and GIF images only. Multi-frame inputs are rejected as decoded-resource policy violations before full decode.
- Accepted optimized images have fixed decoded limits: 8,192 pixels per dimension, 12,000,000 pixels, exactly one frame, and a 64 MiB conservative estimate calculated as width × height × 4.
- `Optimize=false` remains byte-for-byte pass-through and performs no image identification or decode.
- The proxy uses one dedicated ImageSharp allocator with a 128 MiB allocation-group limit and 128 MiB retained pool. These allocator settings are defense in depth, not a cumulative request or process-memory quota.
- A bounded Linux x64 observation supports only a tentative 1 GiB rationale when the encoded download ceiling is at most 50 MiB. APNG was not included, precise native allocation was unavailable, and workload peak and complete-command peak measured different scopes. Higher encoded settings retain proportionally more origin data and require a proportionally larger host-memory budget.

Tile Provider Settings (Admin UI)
- **Tile Provider** — select from presets (OpenStreetMap, Carto Light/Dark, ESRI Satellite) or configure a custom URL template.
- **Custom URL Template** — use `{z}`, `{x}`, `{y}` placeholders; optionally `{apikey}` for providers requiring authentication.
- **API Key** — stored securely for compatible Custom providers that require it.
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
- `DataProtection:KeyRingPath` is the persistent key authority for Identity and protected administrator/personal provider credentials. The supported systemd deployment explicitly retains its existing `/home/wayfarer/.aspnet/DataProtection-Keys` authority; backup requirements are in [Personal Location Providers](24-Personal-Location-Providers.md).
- `LocationProviders:Geoapify:RollingCreditLimit` defaults to 2,500 credits. `LocationProviders:Mapbox:PermanentGeocodingLimit` and `LocationProviders:Mapbox:DirectionsLimit` configure separate Wayfarer safety counters. Mapbox retained geocoding also requires explicit versioned Permanent consent, verification, and selection; disabling a guard may incur charges.

Geoapify uses one rolling pool across persistent reverse geocoding and routing. The 2,500 default retains headroom below the 3,000-credit Free-plan context retrieved 2026-08-23; Wayfarer cannot observe external account use or infer a provider reset timezone. Administrators configure the fixed Geoapify adapter and closed stable-ID transport mappings, never a user key.

Resumable enrichment has a fixed 100-contact execution bound. Geoapify wakes at the oldest admission inside the strict PostgreSQL-time 24-hour window plus five seconds; Mapbox Permanent wakes at the next Wayfarer UTC month boundary plus five seconds. Retry backoff and budget wakes remain distinct. There is no separate queue, scheduler, polling interval, or provider reset-timezone setting.

Mobile
- `MobileGroups:Query:DefaultPageSize` and `MaxPageSize` — paging for mobile group queries.
- `MobileSse:HeartbeatIntervalMilliseconds` — SSE keepalive interval.

Reverse Proxy
- Forwarded headers set in `Program.cs` for nginx or similar. Adjust trusted proxies/networks per environment.

Upload Size
- Effective upload size is enforced via `ApplicationSettings.UploadSizeLimitMB` in DB and `DynamicRequestSizeMiddleware`.

Secrets
- Keep tokens, API keys, and passwords out of `appsettings*.json` in production. Use environment variables or secret stores.
