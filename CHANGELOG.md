# CHANGELOG

## [1.9.7] - 2026-09-04

### Fixed
- Restored durable Geoapify geocoding and directions provider selection, corrected validation placement, and accepted bounded provider snapping and documented routing response semantics in verification, Trip routing, and Mobile routing.

## [1.9.6] - 2026-09-04

### Fixed
- Geoapify Directions now accepts the provider's documented JSON `{lon, lat}` route geometry. Configured providers remain visible, and credential changes retain saved geocoding and directions choices while safely requiring reverification (#547).

## [1.9.5] - 2026-09-04

### Changed
- Personal provider setup at `/User/LocationProviderSettings` now provides independent Geocoding and Directions workflows with explicit verification and provider selection. A credential alone does not authorize contact, each capability is verified and selected independently, and no administrator routing template is required (#538 step 1).
- Geoapify Directions now requires an explicit provider-native mode. Transport Profiles remain independent manual-planning provenance and do not infer provider modes; released Mobile clients remain compatible only through the bounded exact stable-key adapter for omitted modes (#538 step 1).
- Mapbox Permanent Geocoding remains supported under its existing consent and provider-policy contract. Mapbox Directions remains unsupported (#538).
- Production source deployment now selects `Wayfarer.Models.ApplicationDbContext` explicitly when applying EF migrations in a repository with multiple contexts (#535).

### Fixed
- Geoapify capability verification now uses meaningful fixed public probes, production response parsing, bounded known- and unknown-length reads, and safe actionable outcomes. Displayed usage is truthful local Wayfarer-admitted usage, not provider-confirmed billing (#537).

### Removed
- Retired legacy administrator routing providers, templates, Transport Profile mappings, global routing authority, duplicate user routing credentials, generic OSRM execution, `/User/RoutingSettings`, `/User/ApiToken`, and their administrative and user interfaces (#538 step 3).
- The retirement migration deletes obsolete routing ciphertext and configuration while preserving accepted Segment route provenance, saved geometry, and independent Transport Profiles (#538 step 3).

### Upgrade notes
- Before upgrading, back up PostgreSQL and the matching complete Data Protection key ring. Apply migrations before starting the new scheduler/application.
- Rollback across the routing-authority retirement migration requires restoration of the compatible pre-upgrade database and its matching key ring; deploying older binaries alone cannot reconstruct deleted credentials, templates, mappings, selections, or global configuration.

## Unreleased

### Fixed
- Incomplete Geoapify-enriched Locations can now be explicitly repaired without overwriting existing address values; the import workflow reports these rows separately from permanently deferred failures (#559).
- Missing-address enrichment now distinguishes rows available for explicit retry from invalid-coordinate rows, reports the exact accepted retry count, and provides durable feedback when no rows remain eligible (#550).
- Map and Trip popups now keep meaningful detected-place names while replacing technical reverse-geocoding feature types with plain-language address-precision guidance only for broad results (#551).
- Missing-address enrichment now exposes intermediate committed progress through its existing authenticated SSE refresh channel by using smaller durable workflow checkpoints (#554).
- Detailed country, region, and city statistics now tolerate partial address hierarchies on both Timeline views and report failures through Wayfarer's custom alert surface (#557).

## [1.9.4] - 2026-08-31

### Added
- Added protected per-user location-provider profiles with explicit credential verification, independent geocoding and routing selection, revocation, provider-neutral status, and shared bounded usage admission. Legacy Mapbox credentials migrate without exposing or deleting them, but remain inactive until explicitly authorized and selected (#499).
- Added explicitly authorized Mapbox Permanent Geocoding for persistent Location and Place enrichment. Consent is bound to the verified credential generation, accepted results retain provider and storage provenance, and incomplete or changed authority fails closed without blocking tracking, synchronization, imports, Trips, or existing enrichment (#501).
- Added personal Geoapify reverse geocoding and persistent enrichment, plus provider-neutral routed geometry with stable profile mappings, accepted-route provenance, Geoapify/OpenStreetMap attribution, and storage-authorized offline reuse. Geocoding and routing share one guarded per-user allowance, and failed or invalid responses cannot replace retained accepted data (#502).
- Added durable Quartz-backed enrichment workflows for large location-history imports, with bounded batches, Start/Pause/Resume/Cancel/Retry controls, allowance-aware wake-up, authenticated progress, restart reconciliation, and privacy-safe diagnostics. Import completion remains separate from explicitly opted-in enrichment (#507).
- Added explicit-submit Trip Editor place search through the active personal Geoapify authority. Provider contact consumes the shared allowance, authority-bound cache reuse is free, and visibly attributed public Nominatim fallback is limited to authorized no-selection, Mapbox-selection, or exhausted-Geoapify cases; invalid active Geoapify authority fails closed (#526).
- Added optional provider-returned named feature metadata for reverse-geocoded Locations and Trip Places, preserving authenticated provenance and supported import, export, clone, API, and presentation round trips. It remains separate from retained addresses and user-authored labels and does not perform nearby-place discovery (#518).
- Added authenticated, provider-neutral Mobile routing-profile discovery with catalog identity through chooser and capability confirmation, selected-profile execution authority, and separate current Segment profile identity. Discovery contacts no provider, consumes no credit, and remains compatible with released Mobile clients; Mobile-side retained-route ownership is released separately (#528).

### Changed
- Location-history imports now stream and persist bounded batches for Google Timeline JSON, Wayfarer GeoJSON, CSV, GPX, and location-history KML, with durable replay/deduplication, restart recovery, deletion fencing, opaque staging names, and bounded diagnostics. Unsupported generic GeoJSON is rejected, while Trip import remains a separate workflow (#507).
- Geoapify routing now parses documented per-leg geometry, translates step indices across flattened legs, preserves structural waypoint provenance, rejects oversized or invalid anchor sets before usage admission or provider contact, and rejects malformed provider responses before proposal acceptance or persistence (#517).
- New additive migrations introduce protected provider profiles and usage state, Mapbox consent/provenance, Geoapify accepted-route provenance, durable enrichment/import lifecycle authority, optional feature metadata, and concurrency-safe compatibility-profile creation. Deployments must preserve and restore PostgreSQL together with the matching Data Protection key ring and apply the ordered migrations before scheduler startup; production deployment and migration remain pending (#499, #501, #502, #507, #518, #505).
- Provider contact, persistent storage, billing admission, and attribution are explicit authority boundaries: credentials alone grant no contact or storage permission; admitted external contacts remain charged after timeout or failure; Geoapify/OpenStreetMap and Nominatim attribution remains visible where their results are used (#499, #501, #502, #526).
- Finalized durable provider, import/enrichment, Mobile-routing, migration, deployment, backup/restore, and rollback guidance for the backend-first rollout. GitHub release publication, deployment, production migration, and the released-Mobile compatibility smoke remain pending separate authorization (#505).

### Fixed
- Corrected protected group invitation and membership notifications to use authenticated per-user streams with content-free reload hints, authoritative revocation, bounded reloads, and no caller-selected or cross-user channel disclosure (#514).
- Corrected concurrent same-key compatibility transport-profile creation so Segment inserts reuse one deterministic database identity, while deterministic UUID collisions belonging to a different normalized key remain rejected (#505).

### Security
- Isolated personal provider credentials behind the retained Data Protection authority, masked them across user and administrative surfaces, removed query/key-bearing provider URI logging, and required explicit current authority before provider contact or publication (#499, #501, #502, #526, #528).
- Hardened import, enrichment, routing, and notification diagnostics and progress channels so credentials, raw provider responses, coordinate-bearing URLs, personal search text, staging names, and cross-user state are not disclosed (#507, #514).

## [1.9.3] - 2026-08-22

### Fixed
- Corrected the guarded PostgreSQL clone-workflow fixture to reuse its persisted API user instead of attempting a duplicate identity insertion, restoring deterministic relational release validation (#495, #497).

## [1.9.2] - 2026-08-22

### Changed
- Added authenticated per-user idempotency-key handling to CSV and Wayfarer GeoJSON location imports so mobile offline-queue recovery imports and normal location delivery converge on one server record while retaining legacy keyless import behavior (#244, #494).

## [1.9.1] - 2026-08-21

### Changed
- Aligned the opt-in PostgreSQL KML import fixtures with the v1.9 generic/native classification contract while retaining focused rollback, upsert, conflict, concurrency, and recovery protection (#487, #491).

### Fixed
- Added themed hover tooltips to active Segment route badges so A/B/C anchors identify their Start/Via/End role and Place name consistently in the Trip Editor and Viewer (#489, #490).

## [1.9.0] - 2026-08-20

### Added
- Added ordered saved-place waypoints within a single trip segment, including accessible web authoring, all-anchor fallback routes, anchor-aware custom geometry, readable/viewer presentation, clone support, and backward-compatible public API exposure (#388, #403–#414).
- Added Wayfarer-native KML schema v2 round trips for waypoint identity, route indices, transport profiles, measurements, and Automatic/Manual provenance while retaining native v1 compatibility and exact waypoint-free generic KML behavior (#413, #414).
- Added active Segment route presentation across the Trip Editor and Viewer, including Start/Via/End trails, direction cues, route-order badges, synchronized selection, draft-only route reversal, and readable/print parity (#389, #442).
- Added external route generation for administrator-approved providers, with configurable provider pacing and optional personal credentials that remain isolated from server-owned secrets (#426, #448, #449, #451, #453, #454).

### Changed
- Replaced the fixed runtime transport-mode catalog with administrator-managed database profiles and made saved route geometry plus explicit measurement provenance authoritative for segment estimates (#403, #405).
- Updated vulnerable and supported frontend/backend dependencies and development toolchains, including Vite 8, stable TypeScript 6 typechecking, aligned Playwright packages, Swashbuckle 10, and .NET 10-compatible test tooling (#421, #465–#475).
- Added fixed vertex, fidelity, and processing budgets for oversized generic KML routes before persistence, with exact endpoint preservation and bounded simplification reporting during import (#425, #444).
- Aligned fresh and existing PostgreSQL Quartz schemas with the pinned Quartz 3.19 contract under transactional, serialized startup validation (#478, #484).
- Corrected project documentation by removing unsupported MBTiles and offline mobile-map claims.

### Fixed
- Fixed Trip Editor rich notes so ordered and bullet lists no longer recreate or persist empty terminal items, while canonical spacing remains consistent across editor, viewer, readable, print, and PDF surfaces (#433, #435).
- Fixed Trip Editor Region disclosure controls so Regions required by active editor, selection, or search context clearly remain expanded without mutating the user's saved collapse choices, including after reorder recovery (#432, #436).
- Gave the Trip Editor map one stable accessible identity with accurate default, Place, Area, and Segment map-work descriptions (#437, #439).
- Contained the global navbar at browser-zoom widths by using the native collapsed navigation state while preserving keyboard access and long account/menu content (#441, #443).
- Bounded decoded dimensions, frame counts, and memory estimates for optimized public image-proxy inputs while retaining still-image compatibility (#476, #477).
- Corrected Segment route chevron ownership, sizing, contrast, and route-badge layout after zoom and visibility changes (#479, #482).
- Preserved custom route geometry when adding or removing intermediate saved places instead of replacing the route with straight lines (#481, #483).
- Stopped treating generic KML route titles as transport authority; imported routes remain unassigned and show one post-import reminder to select transport modes where needed (#480, #486).

## [1.8.1] - 2026-07-26

### Fixed
- Restored generated trip map thumbnails on deployments using restrictive public `AllowedHosts` by keeping browser capture on loopback with an authorized Host, rejecting failed navigation, and publishing completed JPEGs atomically (#401).

## [1.8.0] - 2026-07-26

### Added
- Added explicit Interactive, Conservative, and Custom/Provider Agreement tile traffic modes. Interactive removes Wayfarer-originated rate-token and per-client cold-series throttling for ordinary human-driven map use while retaining bounded concurrency, queues, caching, coalescing, retries, and provider-directed safety gates (#396, #397).

### Changed
- Increased Interactive map responsiveness to the existing 12-request global and six-request per-client scheduler bounds, while Conservative mode provides explicit 12-contact/second, burst-40, concurrency-eight, and 480-series/client/minute safeguards (#396, #397).
- Removed the incompatible Thunderforest and CARTO presets and fail closed for their known endpoints without deleting preserved configuration or provider-scoped cache data. Corrected the OpenTopoMap preset attribution and documented exact single- and multi-host `AllowedHosts` deployment configuration (#396, #397).
- Existing supported built-ins migrate deterministically to Interactive mode and compatible Custom settings remain preserved. Traffic-mode changes do not change cache identity and require no cache purge (#396, #397).

### Fixed
- Corrected manual Trip Editor place positioning so Add Place preserves the current high-zoom viewport, initializes one authoritative marker at the map center, and retains click, drag, styling, Done, Cancel, Reset, Save, and responsive mobile behavior without duplicate markers (#386, #398).
- Kept edited segment routes visible after map-work Done and before Save, with deterministic persisted/draft/work ownership, failure-safe lifecycle handling, distinguishable route states, and contained docked/mobile segment controls (#387, #399).

## [1.7.0] - 2026-07-25

### Added
- Added provider-aware tile policies with distinct built-in profiles, bounded custom-provider controls, HTTP/2 preference with HTTP/1.1 fallback, and an explicit Admin notice for deployments retaining the historical 30-per-minute cold-miss allowance (#385, #394).

### Changed
- Increased Wayfarer's default interactive provider profile to 6 sustained requests per second, burst capacity 20, and concurrency 6 while retaining bounded queues, per-client protection, provider-directed backoff, caching, cancellation, and the prohibition on OSM prefetch or offline downloads (#385, #393, #394).
- Prioritized visible cold tiles over stale background refresh, coalesced duplicate cold misses, isolated cache and in-flight work by provider identity, and made cold viewports fill progressively instead of entering synchronized 503 retry waves (#385, #393).

### Fixed
- Corrected upstream retry and status handling so every real contact consumes provider capacity, permanent responses are not retried, transient failures remain transient, and provider `Retry-After` instructions are honored without cancellation or privacy regressions (#385, #391).
- Unified sanitized provider attribution across the Trip Editor, authenticated/public/embedded Trip Viewer maps, readable snapshots, and PDF output, including the required OpenStreetMap copyright link without mislabeling custom providers (#385, #392).

## [1.6.0] - 2026-07-22

### Added
- Added deterministic route numbering for regions and places in the Trip Editor, normal trip viewer, readable view, and readable browser print output. Reordering updates and saves the numbering automatically, while raw names remain unchanged.
- The visible Unassigned Places region is fixed at `0`, and place numbering restarts from `1` within every region.

## [1.5.2] - 2026-07-20

### Fixed
- Restored the public Trip Viewer map across phone, tablet, and desktop layouts, contained the phone sidebar within the viewport, and kept the trip area centered through sidebar collapse and expansion (#379)

## [1.5.1] - 2026-07-18

### Fixed
- Reflected the previously accepted live-sharing confirmation in timeline settings so unrelated changes, such as the timeline title, can be saved normally (#377)

## [1.5.0] - 2026-07-18

### Added
- Added an optional custom timeline heading in user settings, with a clear fallback to the existing display-name heading (#356)

### Changed
- Standardized the shared footer's responsive layout and link styling across standard pages (#365)
- Improved phone containment for account settings, API tokens, location imports, and user/manager group tables and maps (#370, #371, #372)
- Updated Vite to 7.3.6 and transitive esbuild to 0.28.1 to resolve Windows development-server security advisories (#360)

### Fixed
- Reconciled global tags safely during trip imports and replaced raw import-error output with safe user-facing feedback (#354)
- Made public timeline sharing fail closed for invalid thresholds and stopped active SSE streams after eligibility is revoked (#363)

## [1.4.1] - 2026-05-21

### Changed
- Moved the shared footer version next to the copyright year and made the year use UTC so deployed pages show the current year without duplicating `Wayfarer` at the end of the footer (#330)

## [1.4.0] - 2026-05-21

### Added
- Added a single compiled Wayfarer runtime version source backed by `Version.props`, exposed through the app CLI, `/api/version`, `X-Wayfarer-Version`, and the shared footer display (#322, #325)
- Added repo-local release helper automation for deterministic version bump preparation, changelog skeleton insertion, offline release checks, explicit local tag checks, and explicit GitHub release validation (#324, #326)
- Added app CLI help output for `help`, `version --help`, and reset-password usage without starting the web host or database-backed command path (#327, #328)

### Changed
- PDF trip export cover snapshots now use the shared image proxy/cache pipeline instead of direct raw cover-image downloads, preserving proxy validation, cache reuse, and origin-work coordination (#319, #323)

## [1.3.2] - 2026-05-20

### Changed
- Map tiles from the local cache now render immediately when stale while bounded background refresh revalidates them with the provider, preserving OSM-safe conditional requests and avoiding request-path waits on cached map views (#316, #318)
- Proxied trip and region images now serve stale optimized local cache files immediately while refresh and cache-miss work is coalesced per image and protected by a process-wide origin/ImageSharp budget (#317, #320)
- Local proxied-image cache hits now bypass anonymous image-proxy rate limiting, while origin downloads and optimization work remain protected for both anonymous and authenticated users (#317, #320)

### Fixed
- Fixed repeated slow tile reloads for cached map areas by moving expired tile revalidation off the response path, adding bounded retry/backoff, atomic tile replacement, and Trip Editor retry/concurrency parity (#316, #318)
- Fixed slow repeated proxied image loads for cached trip images by keeping expired local files usable, refreshing them in bounded background work, and preserving old bytes/metadata on refresh failures (#317, #320)
- Fixed raw cover fallback render paths for public trip thumbnails, quick views, and legacy trip/region cover views so they use the local image proxy instead of direct external image URLs (#317, #320)

### Deferred
- Trip export snapshot cover downloads still use the raw cover URL and are tracked separately for a later release (#319)

## [1.3.1] - 2026-05-20

### Changed
- Improved the Trip Editor visit progress/history modal with region progress bars, clearer filter controls, status icons, visit-count pills, and compact first/last/history rows (#309, #311)
- Added a phone-only Trip Editor map-first bottom drawer with `Trip`, `Regions`, and `Segments` tabs, deterministic drawer states, mobile search placement, dirty-editor guards, and protected desktop/tablet breakpoints (#312, #313, #314)
- The `reset-password` admin CLI now runs as a scoped command host and exits after completion instead of continuing into normal web app startup (#306, #307)

### Fixed
- Fixed Trip Editor public/progress URLs so editor-generated links use the canonical public trip route instead of `/Public/TripViewer/View/{id}` (#308, #310)
- Fixed the `reset-password` CLI service setup to avoid the ASP0000 `BuildServiceProvider` warning while preserving normal Identity password reset behavior (#306, #307)
- Fixed Trip Editor mobile drawer polish issues found during published-bundle validation, including transparent sticky edit headers/footers, non-draggable handle affordance, inconsistent drawer controls/heights, and desktop-to-phone Trip edit/view resize behavior (#314, #313)

## [1.3.0] - 2026-05-19

### Added
- Replaced the legacy Trip Edit experience with the Vue/Vite Trip Editor on the canonical `/User/Trip/Edit/{id}` route (#236, #238, #240, #242, #244, #246, #252, #258, #259, #263, #264, #265, #268, #271, #278, #280, #281, #282, #283)
- Added Trip Editor support for metadata, regions, places, areas, segments, tags, share-progress settings, visit progress/history, rich notes, geosearch add-place, coordinate picking, map navigation, map utilities, and shared docked/expanded editor surfaces (#238, #240, #242, #246, #252, #258, #259, #263, #264, #265, #268, #271, #278, #280)
- Added searchable icon and marker-color selectors, selected-place map/sidebar/status synchronization, popup/marker parity, and responsive light/dark Trip Editor polish (#288, #289, #290, #291, #292, #294)
- Added real endpoint Trip Editor contract coverage for CRUD persistence, search-add persistence, rich-notes persistence, stale/error/dirty/delete feedback, and Development/published asset smoke checks (#297, #298, #299, #300, #301, #302, #303, #304)

### Changed
- `GET /User/Trip/Edit/{id}` now serves the Vue Trip Editor directly; the old `/User/Trip/Workspace/{id}` route and legacy editor fallback were removed during cutover cleanup (#282, #283)
- Trip Editor rich notes are normalized at the Trip Editor request boundary before persistence, including canonical image URL handling, allowed Quill formatting/alignment, unsafe attribute stripping, and trailing helper paragraph cleanup (#302)
- Deployment now requires the Trip Editor Vite assets to be built before `dotnet publish`; server-build deployments need Node.js/npm build tooling and must generate `wwwroot/vite/trip-editor/manifest.json` (#287, #303)
- Development mode uses the Vite dev server assets only, while published/non-Development mode loads manifest-based Trip Editor bundle assets (#303)
- Trip Editor Playwright coverage now distinguishes real endpoint contract proof from mocked UI/request-shape/visual tests (#297, #298)

### Fixed
- Fixed published-output Trip Editor asset loading by moving the Vite manifest to the publish-safe `wwwroot/vite/trip-editor/manifest.json` path (#287)
- Fixed ASP.NET static asset serving/compression interactions for generated `/dist` and `/vite/trip-editor` bundle outputs (#285)
- Fixed source-tree Production-run documentation by documenting published-output acceptance as the supported production-like local test path (#286)
- Fixed Trip Editor release-candidate parity regressions found during manual validation, including Unassigned Places behavior, geosearch add-place defaults and persistence, marker/icon rendering, map search result containment, coordinate pick behavior, copy-link feedback, and selected editor/sidebar state coherence (#288, #289, #290, #291, #292, #294, #296)
- Fixed final Trip Editor E2E nondeterminism around copy-link feedback timing, inline segment editor layout overlap, sidebar search fixtures, visual-polish expand targeting, real CRUD cleanup, and rich-notes image assertions (#304)

## [1.2.28] - 2026-04-13

### Added
- `TileMetadataHotCacheSizeMB` application setting with Admin Settings UI support and client-side derived entry-count hint for the zoom `>= 9` tile metadata hot cache (#217)

### Changed
- Warm zoom `>= 9` tile hits now use an in-process metadata hot cache to avoid the per-hit Postgres metadata read on the common fresh-cache path (#217)
- Hot metadata cache invalidation now participates in purge, eviction, and tile-delete paths while preserving DB/file durability ordering and existing revalidation behavior (#217)

### Fixed
- Fresh hot-hit `LastAccessed` throttling is now atomic per tile and retries immediately after failed DB persists instead of suppressing writes for the full cooldown window (#217)

## [1.2.27] - 2026-03-27

### Fixed
- **HIGH:** LRU/full cache purge timed out on large caches (~500MB), showing error page despite successful deletion. Purge now runs in background with immediate HTTP 202 response (#207)
- Cache lock contention during purge blocked concurrent tile writes. Reduced file-delete chunk size from 100 to 10 with `Task.Yield()` between chunks to prevent writer starvation (#207)

### Added
- SSE-based real-time progress reporting for cache purge operations — admin UI shows animated progress bar with file count and percentage (#207)
- Atomic purge-in-progress guard (`Interlocked.CompareExchange`) prevents concurrent purge operations; second request returns 409 Conflict (#207)
- `TileCachePurgeSse` endpoint for SSE subscription and `TileCachePurgeStatus` endpoint for on-load reconnect (#207)
- On page load, admin settings UI checks purge status and reconnects SSE if a purge is mid-flight (#207)
- Tile-provider-change purge now respects the concurrency guard — skips gracefully if manual purge is running (#207)

### Changed
- `DeleteAllMapTileCache` and `DeleteLruCache` endpoints return HTTP 202 Accepted (was 200 with awaited result) (#207)
- `PurgeAllCacheAsync` and `PurgeLRUCacheAsync` accept optional `SseService`/channel params for progress broadcasting (#207)

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
