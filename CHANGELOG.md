# CHANGELOG

## [1.1.4] - 2026-03-01
- Added trip progress share link toggle and copy button to trip Viewer page (#170)
- Added copy progress link option to trip Index public dropdown (#170)

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
