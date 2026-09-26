# Mobile App

The WayfarerMobile companion app connects to your self-hosted Wayfarer server for GPS tracking, trip navigation, and real-time updates.

---

## Overview

- Built with **.NET MAUI** for cross-platform support
- Streams locations to your own server
- Supports offline maps via tile caching
- Real-time updates via Server-Sent Events (SSE)

---

## Getting Started

### Connect to Your Server

1. Open **Settings** in the mobile app.
2. Use the **QR scanner** to prefill Server URL and API Token, or enter manually.
3. Set your **Server URL** (domain or IP of your Wayfarer instance).
4. Create an **API token** in the web app under your account settings.
5. Paste or scan the token into the mobile app.
6. **Test connection** — the app fetches server settings and activity types.

---

## Location Tracking

### Live GPS Tracking

- Toggle **Timeline Tracking** to enable/disable background location logging.
- Configurable tracking intervals (follows server settings).
- Threshold-aware: only uploads when movement exceeds configured distance.
- Battery-efficient: respects device power settings.

### Manual Check-In

- Record a location on demand with a single tap.
- Rate-limited to prevent spam (server-enforced).
- Useful for specific point-in-time location records.

### Activity Types

- Tag locations with activity types (walking, driving, eating, etc.).
- Activity types sync from server.

---

## Groups

- View **group members** and their locations on a shared map.
- **Accept or decline** invitations from group owners.
- **Subscribe to live updates** via SSE.
- See member locations in real-time.

---

## Trip Navigation

- View your **saved trips** with regions, places, and segments.
- Navigate to places using device navigation apps.
- Download trip tiles for **offline access**.
- View trip details including notes and travel modes.

Waypoint authoring and semantic Via navigation are currently web-first. The additive public trip fields keep existing field types unchanged, and effective waypoint-bearing geometry remains available to older mobile clients, so representative older clients can degrade to their existing From/To route display. Mobile notes-only updates do not replace or erase the server-owned waypoint aggregate.

This is not cross-platform waypoint parity. The mobile app does not currently provide semantic Via identity, offline waypoint fidelity, or waypoint-aware navigation. External route generation is not currently supported from this trip view.

---

## Offline Maps

### Tile Caching

- **Download tiles** for selected trips before traveling.
- Configure **cache size** in Settings.
- Set **prefetch radius** around trip areas.
- Tiles persist for offline use without connectivity.

### Tile Server Configuration

- Default: OpenStreetMap tiles via your Wayfarer server.
- Configurable tile server URL.
- Respect usage policies of tile providers.
- WayfarerMobile never receives personal provider credentials and contacts only its configured Wayfarer server for hosted routing. Discovery now adds the active provider's closed native directions modes while retaining the released client's profile fields. Updated clients explicitly submit one discovered mode; the released client alone may omit it, in which case the server maps only exact built-in keys (`walk`, `bicycle`, `bike`, `car`, or `bus`). Catalog and selected-authority identities fence the exact executable mode. Unknown or free-form keys remain bounded unavailable without provider contact, while retained routes and Direct remain unchanged.
- Saved Segment geometry remains authoritative. When no saved geometry is available, matching validated Wayfarer guidance may be retained locally and reused offline, and users can explicitly request a fresh Wayfarer route. Direct guidance remains explicitly selectable and network-free. Public OSRM and direct routing-provider fallback are not used.
- Missing or unavailable routing remains local to navigation: it does not invalidate authentication or synchronization, and saved Segment geometry and Direct guidance remain available. Hosted routing and bounded retained/offline routing become available in production when the coordinated backend and mobile release is deployed and published; see [Personal Location Providers](24-Personal-Location-Providers.md).

---

## Real-Time Updates (SSE)

The app subscribes to Server-Sent Events for instant updates:

- **Location updates** — see group member movements
- **Visit notifications** — when you arrive at planned places
- **Invitation notifications** — new group invitations
- **Membership changes** — group member updates

SSE automatically reconnects on connection loss.

---

## Privacy & Security

- **You control your data** — all data stays on your server.
- **API tokens** provide secure authentication.
- Rotate tokens if exposed or compromised.
- Server URL stored securely on device.

---

## Settings

| Setting | Description |
|---------|-------------|
| Server URL | Your Wayfarer instance address |
| API Token | Authentication token from web app |
| Tracking Enabled | Toggle background location logging |
| Cache Size | Maximum tile cache size |
| Prefetch Radius | Area around trips to preload tiles |

---

## Troubleshooting

### Connection Issues

- Verify server URL is correct and accessible.
- Check API token is valid (create a new one if needed).
- Ensure device has network connectivity.

### Location Not Updating

- Check tracking is enabled in app settings.
- Verify location permissions are granted.
- Check battery optimization isn't killing background services.

### Tiles Not Loading

- Verify server is accessible.
- Check tile cache isn't full.
- Clear cache and re-download if corrupted.


## Rich-note safety and compatibility

The server uses one AngleSharp-based `RichNotes.Normalize` authority for Trip,
Region, Place, Area, Segment, Location and Visit notes. Independently supplied
writes are canonicalized before persistence; historical values are canonicalized
without saving changes when published through HTML, editor/read DTOs, mutation
echoes, print/PDF HTML or exports. No database migration or historical rewrite is
required. Plain descriptions (including Group descriptions) remain plain text.

Supported HTML includes paragraphs, headings 1–6, blockquotes, line breaks,
strong/emphasis/underline/strike (including basic `b`, `i`, `strike` aliases),
anchors, spans, lists and images. Quill ordered/bullet `data-list`, serif/monospace
fonts and center/right/justify alignment survive. Left alignment uses the default.
Interior blanks and image-only notes survive; terminal empty editor paragraphs and
list items are trimmed. Scripts, event handlers, active resources, arbitrary
classes, styles and attributes are removed. Arbitrary imported table/layout/style
HTML is not a supported fidelity contract.

Links allow HTTP(S), relative/fragment, `mailto:` and `tel:` forms. The full parsed
attribute participates in scheme validation, including embedded browser URL
controls. Custom, file, JavaScript, data and VBScript schemes are rejected. Images
persist only original absolute HTTP(S) URLs. Display `/Public/ProxyImage?url=...`
wrappers are unwrapped deterministically (up to 32 layers; malformed or excessive
nesting removes the image), with one HTML-entity decode by the DOM and ordinary
query ampersands preserved. Display proxying is a later context-safe operation;
its fetch/security policy remains the shared image-proxy authority.

Location is a legacy mixed field whose operational contract is safe rich HTML.
The server does not guess plain versus rich from `<`, and does not interpret
Markdown. The shared GPS/check-in wire field retains this mixed contract; a
known plain source such as generated Google Timeline metadata encodes literal
text before entering it. Native KML carries all five Trip note domains; generic
My Maps descriptions remain rich. XML/CDATA/CSV/JSON encoding is transport only;
exports canonicalize notes before serialization and import does not add an extra
HTML decode. Supported markup round-trips semantically, not byte-for-byte.

Existing wire names and shapes are unchanged. Legacy null/omitted notes remain
no-change, empty values retain their existing replacement behavior, and Location
`ClearNotes` retains explicit clearing. Modern editor replacement keeps its
existing empty-note behavior. Segment notes-only updates do not change routes or
waypoints. The server does not change optimistic queues or mobile local storage.

A server deployment protects subsequent server-controlled reads and writes. It
cannot scrub previously downloaded mobile HTML, device-only imports, old PDFs or
exported files. Some mobile saves keep optimistic local HTML rather than replacing
it immediately with the server response; no device-cache cleanup is implied.
