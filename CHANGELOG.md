# CHANGELOG

## [1.2.26] - 2026-03-27

### Changed
- Outbound budget burst capacity raised from 10 to 12 — allows 2 more tiles through on initial burst before settling into sustained 2/sec rate, reducing 503s on cold-cache loads (#214)
- Outbound budget acquire timeout raised from 3.0s to 3.5s — extra 0.5s yields 1 more token from replenishment per wave, reducing false timeouts (#214)
- Client concurrency pool multiplier raised from 60% to 75% of burst capacity (pool size 6 → 9) — more tiles queue server-side instead of waiting client-side (#214)
- Budget retry-after interval updated from 5s to 6s to align with new burst refill time (12 tokens / 2 per sec = 6s) (#214)
- Client slow-retry interval auto-derives to 18s (was 15s) from updated retry-after × 3 (#214)

## [1.2.25] - 2026-03-26

### Fixed
- **HIGH:** Slow retry phase replayed full 5-attempt fast-retry cycle on each poll (~34s lag per attempt, 6 per-IP budget hits per cycle). Now makes single-shot fetches — one request per slow poll (~15s intervals), one budget hit each (#206)

### Changed
- `_scheduleSlowRetry` now calls `_slowRetryOnce` (single fetch + reschedule on 503) instead of resetting to `_fetchWithRetry` attempt 0 (#206)

### Added
- `_slowRetryOnce` method — lightweight single-fetch slow-phase handler that avoids the overhead of the full fast-retry state machine (#206)

## [1.2.24] - 2026-03-26

### Changed
- Default per-IP outbound budget increased from 30 to 80 cache misses/min — 30 was too low for cold-cache zoom-17 loads (~35 tiles), causing immediate per-IP rejection before retries could succeed (#206)
- Slow retry interval reduced from 30s to 15s — derived from server's `retryAfterSeconds * 3` instead of hardcoded (#206)
- Client-side concurrency pool size now derived from server's `burstCapacity * 0.6` (injected via `wayfarerTileConfig`) instead of hardcoded 6 (#206)
- Slow retry delay now derived from server's `retryAfterSeconds * 3` (injected via `wayfarerTileConfig`) instead of hardcoded 30s (#206)
- `TilesController.BudgetRetryAfterSeconds` changed from `private string` to `internal int` for config injection (#206)
- `TileCacheService.OutboundBurstCapacity` added as public accessor for `OutboundBudget.BurstCapacity` (#206)
- `wayfarerTileConfig` in `_Layout.cshtml` now includes `burstCapacity` and `retryAfterSeconds` from server config (#206)

## [1.2.23] - 2026-03-26

### Fixed
- **MEDIUM:** Tiles that exhausted fast retries on 503 went permanently gray with no recovery path — after the per-IP budget window decayed, those tiles could have loaded but never retried again (#206)

### Added
- Slow retry phase in `retryTileLayer.js` — after 5 fast retries exhaust on 503 or network error, tiles enter indefinite 30-second polling (with ±25% jitter) until they load or are removed; ensures all tiles eventually appear even when per-IP budget temporarily blocks them (#206)
- `_scheduleSlowRetry` method and `slowRetryDelayMs` option (default 30s) on `RetryTileLayer` (#206)

## [1.2.22] - 2026-03-26

### Fixed
- **HIGH:** Cascading 503 on cold-cache tile loading — per-IP outbound budget counter incremented on every request (including those rejected by the global budget), so client-side retries found the counter already past the limit and failed immediately, causing all tiles to gray out permanently (#206)

### Added
- Client-side concurrency pool (6 slots) in `retryTileLayer.js` — tiles queue client-side and stream in progressively instead of blasting ~35 simultaneous requests that overwhelm server budgets (#206)
- Two-phase per-IP rate limiting: `WouldExceedRateLimit` (peek without increment) and `RecordRateLimitHit` (increment only) in `RateLimitHelper` — enables check-then-record pattern where only actual upstream fetches count against the per-IP limit (#206)
- `PeekCount` method on `RateLimitEntry` — read-only weighted sliding-window count for speculative checks (#206)

### Changed
- `SendTileRequestCoreAsync` now uses two-phase per-IP budget: peeks first (fast-fail), then records the hit only after global budget token is acquired (#206)

## [1.2.21] - 2026-03-26

### Added
- `RetryTileLayer` — custom Leaflet TileLayer subclass using `fetch()` for HTTP status code access; retries on 503 with exponential backoff and `Retry-After` header support (#206)
- `createTileLayer()` factory in `retryTileLayer.js` — centralizes tile layer creation, replacing duplicated boilerplate across 13 JS files (#206)
- `TileRetrievalResult` — typed result class distinguishing tile success, not-found, and budget-throttled states (#206)
- `RequestIdLoggingMiddleware` — pushes `HttpContext.TraceIdentifier` into Serilog `LogContext` so every log entry includes `RequestId` automatically (#206)
- Serilog `.Enrich.FromLogContext()` and `{Properties:j}` output templates for console and file sinks (#206)
- `DbMetadataZoomThreshold` constant replacing magic number `9` across `TileCacheService` (#206)
- Inline `tileerror` retry fallback for HiddenAreas Create/Edit views (cshtml inline scripts) (#206)

### Fixed
- **HIGH:** Cold-cache tile loading returned 404 for budget-exhausted tiles — Leaflet treated as permanent failure, showing persistent gray areas. Now returns 503 + `Retry-After` header; client retries automatically (#206)
- **MEDIUM:** Ghost metadata rows stored in DB when tile fetch was aborted by budget exhaustion — rows had `Size=0`, null `ETag`/`ExpiresAtUtc`, pointing to non-existent files (#206)
- **MEDIUM:** Potential blob URL memory leak in `RetryTileLayer` — if a tile was removed (panned/zoomed away) while blob was being read, the revoke callback never fired; now guarded with `signal.aborted` check (#208)
- **LOW:** Client-side retry thundering herd — all 503'd tiles retried at identical intervals; now adds ±25% jitter to backoff delays (#208)
- **LOW:** Zero or negative `Retry-After` header values caused immediate retry; now clamped to base delay floor (#208)
- **LOW:** `CacheTileAsync` upstream failure path relied on downstream null guard; now returns early with explicit intent (#208)

### Changed
- `CacheTileAsync` now returns `bool` (`false` = budget exhaustion) instead of `void` (#206)
- `RetrieveTileAsync` now returns `TileRetrievalResult` instead of `byte[]?` (#206)
- `PerformanceMonitoringMiddleware` log line now includes explicit `RequestId` parameter (#206)
- `BudgetRetryAfterSeconds` constant extracted with doc linking to `OutboundBudget` config (#208)
- `ReadAsByteArrayAsync` and inter-retry `Task.Delay` now pass `CancellationToken` for prompt cancellation (#208)
- `TileCacheServiceTests` and `TilesControllerTests` now share `[Collection("OutboundBudget")]` to prevent parallel test interference (#208)

## [1.2.20] - 2026-03-22

### Added
- Per-IP outbound budget tracking (default 30 cache misses/min/IP) — prevents a single client from monopolizing the global outbound token budget (#204)
- Admin UI field for configuring per-IP outbound budget limit (0 = disabled) (#204)

### Fixed
- **HIGH:** Outbound budget starvation DoS — a single attacker could exhaust all outbound tokens with uncached tile requests, denying service to legitimate users (#204)
- **HIGH:** IPv4-mapped IPv6 addresses not normalized on direct-IP path — `::ffff:x.x.x.x` and `x.x.x.x` created separate rate-limit buckets, bypassing limits (#204)
- **HIGH:** Concurrent insert race in `CacheTileAsync` — two requests for the same uncached tile could trigger an unhandled `DbUpdateException` from the unique index; now caught as a benign race (#204)
- **MEDIUM:** `PurgeBatchAsync` and `PurgeLRUCacheAsync` decremented `_currentCacheSize` using stale projected sizes instead of re-fetched entity sizes, causing cache size drift (#204)
- **MEDIUM:** `PurgeAllCacheAsync` loaded full entities into memory for the metadata dictionary; now projects only `Id` and `TileFilePath` with `AsNoTracking` to reduce memory usage on large caches (#204)
- **MEDIUM:** `RetryOperationAsync` caught all exceptions including non-transient ones; now catches only `DbUpdateException` so non-recoverable errors propagate immediately (#204)
- **MEDIUM-LOW:** Revalidation coalescing captured first caller's `CancellationToken` — client disconnect cancelled outbound request for all coalesced waiters (#204)

## [1.2.19] - 2026-03-22

### Added
- Unique composite index on `TileCacheMetadata(Zoom, X, Y)` — eliminates sequential scans on every tile request (#204)
- Periodic tile cache size reconciliation via `RateLimitCleanupJob` — corrects `_currentCacheSize` drift from non-atomic updates every 5 minutes (#204)
- Hard cap (50K entries) on rate limit caches with oldest-entry eviction — prevents unbounded memory growth from sustained low-rate attacks (#204)
- `CancellationToken` propagation through tile request chain — requests abort when client disconnects instead of blocking threads (#204)

### Changed
- Outbound budget `AcquireTimeout` reduced from 10s to 3s to prevent thread pool starvation under sustained cold-cache load (#204)
- `PurgeAllCacheAsync` loads all DB metadata in a single query instead of O(N) individual queries per file (#204)
- `PurgeLRUCacheAsync` now deletes in chunks of 1000 IDs to prevent PostgreSQL query plan explosion from large IN clauses (#204)
- Eviction and purge file deletion consolidated into single lock acquisition per batch, eliminating convoy effects (#204)
- `X-Forwarded-For` IP addresses normalized to canonical form — prevents IPv4/IPv6 aliasing from creating separate rate limit buckets (#204)
- Eviction `_currentCacheSize` decrement now uses re-fetched entity sizes instead of stale projected sizes (#204)

### Fixed
- **CRITICAL:** Missing database index on hot-path tile lookup queries (`Zoom, X, Y`) — every tile request was a sequential scan (#204)
- **CRITICAL:** `PurgeAllCacheAsync` issued individual DB query per cached file — 100K files caused 100K sequential-scan queries (#204)
- **HIGH:** `_currentCacheSize` drift from eviction using pre-fetched sizes instead of actual deleted sizes (#204)
- **HIGH:** Thread pool starvation risk from 10-second outbound budget timeout under cold-cache load (#204)
- **HIGH:** Lock convoy during eviction/purge — per-file lock acquisition serialized all concurrent writes (#204)
- **MEDIUM:** Sliding-window rate limiter documentation understated worst-case jitter (up to full prevCount, not ~0.5) (#204)

## [1.2.18] - 2026-03-22

### Added
- Sliding-window rate limiter replacing fixed-window — prevents boundary-batching attacks where bursts at window edges could double the effective limit (#204)
- Authenticated user rate limiting by user ID (default 2000 req/min) — previously authenticated users bypassed rate limiting entirely (#204)
- `TileRateLimitAuthenticatedPerMinute` application setting for configurable authenticated tile rate limit, exposed in Admin Settings UI (#204)
- Outbound request budget (token-bucket at 2 req/sec, burst 10) — prevents cache-miss cascading from overwhelming upstream OSM and risking a fair-use block; complies with OSM 2-connection policy via transport-level enforcement (#204)
- `X-Content-Type-Options: nosniff` header on tile proxy responses to prevent MIME-sniffing (#204)

### Changed
- Rate limiter now uses sliding-window counter approximation instead of fixed-window, smoothing request counting across window boundaries (#204)
- Default anonymous tile rate limit increased from 500 to 600 req/min to compensate for the stricter sliding-window algorithm (#204)
- Rate limiting applies to both anonymous (by IP) and authenticated (by user ID) requests with separate configurable thresholds (#204)
- Admin Settings UI updated to show both anonymous and authenticated rate limit fields; removed incorrect "never limited" text (#204)
- Outbound tile requests gracefully degrade (serve stale cache) when upstream budget is exhausted (#204)
- Rate limit cleanup flag is now per-cache instance instead of a shared global flag, allowing independent cleanup of anonymous, authenticated, and image proxy caches (#204)
- `X-Forwarded-For` header values are now validated with `IPAddress.TryParse` before use as rate limit keys (#204)
- Outbound budget `StopReplenisher` now cancels the old CTS before creating replacements, eliminating brief replenisher overlap (#204)

### Added
- `RateLimitCleanupJob` — periodic Quartz job (every 5 minutes) sweeps expired entries from all in-memory rate limit caches, preventing unbounded memory growth (#204)
- Log warning when authenticated user lacks `NameIdentifier` claim and falls back to IP-based rate limiting (#204)

### Fixed
- Eviction `_currentCacheSize` tracking now decrements after successful DB commit, preventing permanent undercount on failed eviction (#204)
- Tile cache eviction now commits DB deletions before deleting files — previously files were deleted first, leaving orphaned DB records pointing to missing files if `SaveChangesAsync` failed (#204)
- Admin settings checkbox hidden-field fallback for `TileRateLimitEnabled` and `IsRegistrationOpen` — unchecking now correctly posts `false` instead of falling back to C# default (#204)
- **CRITICAL:** Remove global read-lock on tile cache — file reads no longer serialize through `_cacheLock`, eliminating a throughput bottleneck under concurrent map viewers. Writes and deletes retain the exclusive lock; reads catch `IOException` as cache miss (#204)
- **CRITICAL:** Increase outbound budget burst capacity from 2 to 10, reducing cold-cache map load times. OSM's 2-connection policy is now enforced at the transport layer via `SocketsHttpHandler.MaxConnectionsPerServer` (#204)
- **HIGH:** Eviction coalescing — concurrent `CacheTileAsync` calls can no longer trigger simultaneous eviction runs (double-evict). Uses `Interlocked.CompareExchange` guard with `DbUpdateConcurrencyException` handling (#204)
- **HIGH:** `EvictDbTilesAsync` now uses a dedicated `IServiceScope` instead of the per-request `_dbContext`, preventing disposed-context failures when eviction outlives the originating request (#204)
- **HIGH:** `CacheTileAsync` no longer retries on outbound budget exhaustion — breaks immediately instead of blocking up to 30 seconds (3 retries × 10s timeout) (#204)
- **MEDIUM:** Admin settings cross-field validation: authenticated rate limit must be >= anonymous rate limit (#204)

## [1.2.17] - 2026-03-22

### Added
- Conditional requests (ETag / If-Modified-Since) for tile cache re-validation — expired tiles send conditional headers to upstream, serving cached data on 304 Not Modified (#201)
- Cache header compliance — parse and honour `Cache-Control: max-age` and `Expires` headers from upstream tile servers, with 7-day default fallback per OSM policy (#201)
- Per-tile request coalescing — concurrent requests for the same expired tile are coalesced into a single upstream HTTP request (#201)
- In-memory sidecar metadata cache for zoom 0-8 tiles, eliminating disk I/O on the hot path (#201)
- Sidecar `.meta` JSON files alongside zoom 0-8 tiles to persist ETag/Last-Modified/expiry across restarts (#201)
- `ETag`, `LastModifiedUpstream`, and `ExpiresAtUtc` columns on `TileCacheMetadata` for zoom >= 9 tiles (#201)

### Changed
- Use canonical OSM tile URL `https://tile.openstreetmap.org/` instead of non-canonical `https://a.tile.openstreetmap.org/` (#201)
- Enforce minimum tile cache size of 256 MB in Admin Settings (OSM requires at least 7 days of cached tiles) (#201)
- Throttle `LastAccessed` DB updates to once per 5 minutes per tile, reducing DB writes by ~99% for popular tiles (#201)
- Graceful degradation: serve stale cached tiles when upstream re-validation fails (#201)

### Fixed
- `MaxCacheTileSizeInMB = -1` (disable cache limit) now correctly skips LRU eviction instead of silently defaulting to 1024 MB (#201)

### Performance
- Single DB round-trip for metadata load + conditional LastAccessed update on zoom >= 9 hot path (#201)
- Request coalescing reduces outbound HTTP requests under concurrent load (#201)
- 304 Not Modified responses avoid re-downloading unchanged tile content (#201)

## [1.2.16] - 2026-03-22

### Fixed
- Fix OSM tile 403 "Referrer is required" by adding per-request Referer header and honest User-Agent to outbound tile proxy requests (#199)

### Changed
- Move HttpClient header configuration from TileCacheService constructor to AddHttpClient DI registration for correct lifecycle management
- Remove redundant AddScoped<TileCacheService> registration (AddHttpClient already registers scoped)
- Use TryParseAdd for User-Agent with fallback when Application:ContactEmail contains invalid RFC 7230 characters
- Update TilesController IsValidReferer doc to clarify it is an abuse deterrent, not a security boundary

### Added
- `Application:ContactEmail` configuration setting for tile provider User-Agent compliance (configurable via systemd env var `Application__ContactEmail`)
- Startup warning when ContactEmail is not configured in non-Development environments
- Deployment template and install.sh support for the new ContactEmail setting

## [1.2.15] - 2026-03-08

### Added
- Server-side pagination for the user trips index page with page navigation (first, last, previous, next, go-to-page) and configurable entries per page (10/25/50, default 10) (#195)
- New `/api/Trips/search` endpoint for paginated trip queries with text search and visibility filtering

### Changed
- Trip index page now loads data via AJAX instead of server-rendering all trips, improving performance for users with many trips

## [1.2.14] - 2026-03-08

### Added
- Trip index page now shows a stats summary bar (total, public, private counts) that updates dynamically with search and filters (#194)

## [1.2.13] - 2026-03-08

### Fixed
- Fix trip images appearing broken until first cache warm-up runs (#193)
  - Reduce debounce delay from 5 minutes to 1 minute
  - Add immediate mode (~5 seconds) for first-time image introductions
  - Schedule warm-up on trip creation, cloning, and API trip updates (previously missing)

## [1.2.12] - 2026-03-08

### Performance
- Add loading="lazy" to proxied images in trip notes to defer off-screen image loading

## [1.2.11] - 2026-03-08

### Fixed
- Fix Wikipedia/Wikimedia images returning 403 when proxied (missing User-Agent header)
- Use admin-configurable image cache expiry for browser Cache-Control headers instead of hardcoded 24h
- Add cache-busting to trip cover image URLs to prevent stale browser cache after URL changes

### Changed
- Max proxy image download size is now admin-configurable (default raised from 20 MB to 50 MB)

## [1.2.10] - 2026-03-08
- Improved image cache read performance by removing global lock serialization from cache hits
- LastAccessed updates are now conditional (only when stale >1 hour) reducing DB writes
- Added background cache warm-up: external images in notes and cover images are pre-cached
  5 minutes after trip/region/place/area save (debounced)
- Extracted ImageProxyService for shared image fetch+optimize+cache logic
- Extracted ImageProxyHelper utility (IsUrlAllowed, ComputeImageCacheKey, OptimizeImage) to
  fix inverted service→controller dependency
- Added dedicated tests for HtmlHelpers.ExtractExternalImageUrls and CacheWarmupScheduler
  TOCTOU fallback path

## [1.2.9] - 2026-03-07
- Fixed map snapshot URL returning 404 due to query string not being stripped from file path
- Added server-side proxy rewriting for external images in trip/region/place notes HTML
- Notes images now load through /Public/ProxyImage cache endpoint instead of directly from external servers

## [1.2.8] - 2026-03-07
- Routed public cover images through ProxiedImageCacheService disk cache instead of raw 302 redirects
- Cover images in public trip grid, list, and Viewer hero are now served via /Public/Trips/{id}/CoverImage endpoint
- Cached cover images benefit from SSRF protection, ImageSharp optimization, ETag/304 support, and LRU eviction
- Private (non-public) trip cover images in Viewer continue to load directly from external URL for the owner
- Extracted shared FetchAndCacheImage pipeline used by both ProxyImage and CoverImage endpoints

## [1.2.7] - 2026-03-07
- Fixed copy cover image and map snapshot URL options showing on public trip page to non-owners (#181)
- These options now only appear for the trip owner in the Viewer dropdown

## [1.2.6] - 2026-03-07
- Added visual hint in backfill "Consider Also" tab for suggested locations already linked to the trip (#183)
- Suggested locations whose place is already confirmed or existing show a green lightbulb icon with tooltip

## [1.2.5] - 2026-03-07
- Added search and filter functionality to user trips index page (#182)
- Search field filters trips by name and notes with debounced input
- Tri-state radio filter for All/Public/Private trip visibility
- Client-side filtering with combined AND logic

## [1.2.4] - 2026-03-07
- Added public endpoints for trip cover image and map snapshot (#181)
- Added GET /Public/Trips/{id}/CoverImage — 302 redirect to cover image URL
- Added GET /Public/Trips/{id}/MapSnapshot — serves map snapshot JPEG directly
- Added GET /api/trips/public/{id}/images — JSON metadata with absolute image URLs
- Extracted shared rate limiting utility (RateLimitHelper) from TripViewerController and TilesController
- All new endpoints are rate limited for anonymous users
- Added copy URL options for cover image and map snapshot in trip Viewer dropdown and User Trip Index public dropdown

## [1.2.3] - 2026-03-07
- Added area stats to trip summaries in list, grid, and quick preview views (#179)

## [1.2.2] - 2026-03-07
- Added site-wide back-to-top button that appears after scrolling, with smooth scroll and theme support (#175)

## [1.2.1] - 2026-03-07
- Fixed trips with no cover image showing broken image instead of map snapshot fallback in grid view (#176)
- Added map snapshot fallback to list view cover image column for trips with coordinates but no cover image (#176)

## [1.2.0] - 2026-03-01
- Added disk-cached image proxy with LRU eviction for proxied images (#169)
- Added SSRF protection to ProxyImage endpoint blocking private IPs and non-HTTP schemes (#169)
- Added Cache-Control and ETag headers to proxied images and tile responses (#169)
- Added response compression middleware (Brotli + Gzip) (#169)
- Added admin-configurable image cache size limit and expiry duration (#169)
- Added image cache stats to admin settings dashboard (#169)
- Updated deployment docs and scripts for ImageCache directory (#169)

## [1.1.4] - 2026-03-01
- Added trip progress share link toggle and copy button to trip Viewer page (#170)
- Added copy progress link option to trip Index public dropdown (#170)
- Fixed flaky SSE broadcast test timing on slow CI runners

## [1.1.3] - 2026-03-01
- Fixed public trips grid view title unreadable in dark theme (#168)

## [1.1.2] - 2026-02-27
- Fixed search clear button not cancelling pending debounce timer (#166)

## [1.1.1] - 2026-02-27
- Fixed region headers not respecting dark theme in trip analysis modal
- Added inline clear button to analysis search field

## [1.1.0] - 2026-02-27
- Improved trip analysis: group results by region and place name across all tabs (#163)
- Added fuzzy search filtering across all analysis tabs (#163)
- Fixed duplicate suggestions in Consider Also tab (#163)
- Increased analysis modal list height responsively for better data visibility (#163)

### 2026-02-21
- Fixed EF Core warnings for First/FirstOrDefault without OrderBy on ApplicationSettings queries (#159)
- Fixed latent crash in LocationImportController when ApplicationSettings table is empty
- Added deterministic ordering to in-memory GroupBy deduplication patterns
- Fixed frontend.config.yaml missing from publish output causing startup warning (#160)
- Upgraded MvcFrontendKit from 1.0.0-preview.24 to 1.0.0

## [2026-02-10]
### Changed
- Bumped HtmlSanitizer dependency from 8.1.870 to 9.0.892 (PR #158)

### 2026-01-26
- Fixed API logging privacy for production release (#157)
- Changed authentication success logs to Debug level (silent in production)
- Removed usernames from logs, replaced with UserId
- Removed token info from success logs (retained in failure logs)
- Downgraded routine operation logs to Debug level

### 2026-01-24
- Restructured documentation for open-source release (#146)
- Added 50+ screenshots throughout user and developer documentation
- Added Docsify theme with Wayfarer brand colors (teal/coral)
- Added local docs serving at /docs/ via ASP.NET static files
- Added Docs and Mobile links to navigation and footer
- Fixed broken internal documentation links
- Fixed missing API endpoint documentation (Icons, Tags, Users, Visit, Backfill)
- Simplified technical jargon in user-facing documentation
- Updated home page tagline to "Track Your Timeline - Manage Your Trips"
- Improved 404 page with larger transparent logo and bigger text

### 2026-01-22
- Added centralized Wikipedia search utility with dual search strategy (#142)
- Combines geosearch and text search for better Wikipedia article discovery
- Migrated 8 files to use new shared module, removing ~600 lines of duplicate code
- Added place context map modal to trip visit analysis (#139)
- Map shows place marker and location pings that contributed to the match
- Includes ruler measurement tool, auto-fit bounds, and ping tooltips with details
- Added "Consider Also" suggestions feature to backfill analysis (#134)
- Added 4-tab interface for backfill modal: Confirmed, Consider Also, Stale, Existing
- Added cross-tier evidence logic to catch near-miss visits while filtering GPS noise
- Added SuggestedVisitDto with tier hit counts and suggestion reasons
- Added VisitedSuggestionMaxRadiusMultiplier setting (default 50×, configurable 2-100×)
- Added derived suggestion tier properties (Tier 1-3 radii and hit requirements)
- Added Source property to PlaceVisitEvent to track visit origin (realtime, backfill, backfill-user-confirmed)
- Added user check-in detection as strong signal for suggestions
- Added admin settings UI for suggestion multiplier with derived tier info panel
- Added unique index on PlaceVisitEvents (UserId, PlaceId, Date) to prevent duplicates at DB level
- Added chunking for batched spatial queries when places > 10,000 (PostgreSQL parameter limit)
- Added CancellationToken propagation to individual place analysis queries
- Added frontend validation for date range (fromDate must be ≤ toDate)
- Fixed potential KeyNotFoundException with TryGetValue pattern for region lookups

### 2026-01-21
- Added Visit Backfill feature to analyze location history and create visit records (#104)
- Added backfill preview with new visits, stale visits, and existing visits sections
- Added confidence scoring based on location count and proximity
- Added stale visit detection (place deleted/moved beyond radius)
- Added manual visit deletion with checkboxes in existing visits
- Added select/deselect all functionality for visit selections
- Added action summary showing what will happen on Apply
- Added Clear All Visits option in trip dropdown menu
- Added navigation from Visit to underlying Location records (#127)
- Added Relevant Locations card on Visit/Edit page
- Added Locations column with lazy-loaded counts in Visit Index
- Added visit notification cooldown setting to reduce SSE spam (#128)
- Fixed duplicate visit prevention with timezone-aware date comparison
- Fixed duplicate detection to check by PlaceNameSnapshot in addition to PlaceId
- Fixed settings persistence for cooldown and rate limit settings (#128)

### 2026-01-20
- Added location metadata fields: accuracy, speed, altitude, heading, source (#121)
- Added import deduplication to prevent duplicate location entries (#121)
- Added metadata support to all location exporters (GeoJSON, CSV, GPX, KML)
- Added metadata parsing to GPX, KML, GeoJSON, and CSV importers
- Added capture metadata display to Location Edit view
- Added test coverage for metadata parsing and deduplication boundaries (#124)
- Fixed Source field extraction in GeoJSON and CSV parsers
- Removed location timestamp unique index that caused import failures (#125)

### 2026-01-19
- Added inline activity view/edit mode for location modals and tables
- Added table activity editing with preselected activity values
- Added cookie-auth fallback for location activity updates
- Added admin tile provider settings with presets, custom templates, and API key support
- Added tile provider validation and cache purge on provider change
- Added dynamic map attribution from the active tile provider
- Added tile request rate limiting for anonymous users (configurable, default 500/min per IP)
- Added X-Forwarded-For support for correct IP detection behind reverse proxies
- Added tile coordinate validation (z: 0-22, x/y: 0 to 2^z-1)
- Fixed XSS vulnerability in tile provider attribution via HTML sanitization (#115)
- Fixed race condition in tile cache size tracking with Interlocked operations (#115)
- Fixed API key exposure in tile service logs via URL redaction (#115)
- Fixed X-Forwarded-For spoofing by only trusting header from localhost/private IPs (#115)
- Fixed race condition in rate limiter with atomic ConcurrentDictionary operations (#115)
- Fixed rate limiter TOCTOU on window reset with CompareExchange (#115)
- Fixed tile cache lock not being shared across scoped service instances (#115)
- Fixed tile cache size not initialized from database on startup (#115)
- Fixed file read race condition after CacheTileAsync (#115)
- Fixed synchronous DB query in GetLruCachedInMbFilesAsync (#115)
- Fixed group map selection filters to honor Show/Hide All and historical visibility (#117)
- Security: Added HtmlSanitizer for safe attribution rendering
- Security: Added CSRF protection to cache deletion endpoints (#115)
- Security: Added anti-forgery tokens to cache deletion AJAX calls (#115)

### 2026-01-17
- Added CHANGELOG.md

### 2026-01-14
- Fixed popup dark theme styling
- Fixed API endpoint DTO responses (#101, #102)

### 2026-01-11
- Added location idempotency keys for duplicate prevention
- Fixed area notes layout stretch

### 2026-01-10
- Added GPS accuracy threshold filter for location logging
- Added PUT endpoint for updating trip areas
- Expanded admin threshold options (time and distance)
- Fixed GPS accuracy threshold persistence (default now 50m)
- Fixed duplicate location markers from race conditions (#93)
- Moved threshold display to User Settings page (#85)

### 2026-01-03
- Reduced check-in rate limit from 30s to 10s

### 2026-01-01
- Added user display name in navigation
- Fixed groups marker popup showing wrong user
- Fixed dark theme QR code readability for 2FA

## 0.9

### 2025-12-31
- Exposed accuracy and speed properties in location views
- Improved marker clustering (exclude live/latest markers)
- Fixed dark theme inconsistencies across multiple views
- Fixed live-to-latest marker transition

### 2025-12-30
- Added API token hashing for secure storage
- Added account lockout to prevent brute-force attacks
- Fixed hashed token authentication for mobile
- Added secrets management via systemd environment variables

### 2025-12-28
- Added real-time job status updates via SSE
- Added job control panel (pause/resume/cancel)
- Added mobile visits recent endpoint for background polling
- Added 3-minute threshold option in admin settings
- Fixed orphan visit cleanup

### 2025-12-27
- Added Visit management feature
- Added visit started SSE notifications
- Fixed visit search case sensitivity
- Fixed visit image sizing and marker size

## 0.8

### 2025-12-21
- Added trip Areas feature with notes and images
- Added route progress tracking

### 2025-12-14
- Added trip Places with route segments
- Added drag-to-reorder for places

### 2025-12-07
- Added trip creation and basic editing
- Added trip privacy controls (public/private)

## 0.7

### 2025-11-23
- Added location clustering for performance
- Added cluster statistics modal

### 2025-11-16
- Added dark theme support
- Added theme toggle in user settings

### 2025-11-09
- Added location search with filters
- Added date range filtering

## 0.6

### 2025-10-26
- Added Google Timeline JSON import
- Added location export (JSON)

### 2025-10-19
- Added reverse geocoding for locations
- Added location editing

### 2025-10-12
- Added hidden areas feature for privacy

## 0.5

### 2025-09-28
- Added Groups feature for sharing locations
- Added group invitations

### 2025-09-14
- Added live location tracking via SSE
- Added latest location marker

## 0.4

### 2025-08-31
- Added public timeline sharing
- Added embeddable timeline widget

### 2025-08-17
- Added user statistics dashboard

## 0.3

### 2025-07-26
- Added API token management
- Added mobile app authentication

### 2025-07-12
- Added location logging API endpoint
- Added distance and time thresholds

## 0.2

### 2025-06-21
- Added user registration and login
- Added two-factor authentication

### 2025-06-07
- Added basic map view with OpenStreetMap tiles
- Added tile caching for fair use

## 0.1

### 2025-05-24
- Added location display on map
- Added location CRUD operations

### 2025-05-03
- Initial project setup
- Basic ASP.NET Core MVC structure
