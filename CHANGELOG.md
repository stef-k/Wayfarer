# Changelog

## Unreleased

### 2026-01-22
- Added "Consider Also" suggestions feature to backfill analysis (#134)
- Added 4-tab interface for backfill modal: Confirmed, Consider Also, Stale, Existing
- Added cross-tier evidence logic to catch near-miss visits while filtering GPS noise
- Added SuggestedVisitDto with tier hit counts and suggestion reasons
- Added VisitedSuggestionMaxRadiusMultiplier setting (default 50×, configurable 2-100×)
- Added derived suggestion tier properties (Tier 1-3 radii and hit requirements)
- Added Source property to PlaceVisitEvent to track visit origin (realtime, backfill, backfill-user-confirmed)
- Added user check-in detection as strong signal for suggestions
- Added admin settings UI for suggestion multiplier with derived tier info panel

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
