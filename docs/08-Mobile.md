# Mobile App

The WayfarerMobile companion app connects to your self-hosted Wayfarer server for GPS tracking, trip navigation, and real-time updates.

Get the Android app from [WayfarerMobile releases](https://github.com/stef-k/WayfarerMobile/releases). The source project uses .NET MAUI for cross-platform support; the published 1.3.0 release is Android, with no corresponding iOS release. You need a reachable Wayfarer server and a User-role account; see [Getting Started](01-Getting-Started.md) or [Install & Self-Hosting](02-Install-and-Dependencies.md).

---

## Overview

- Built with **.NET MAUI** for cross-platform support
- Streams locations to your own server
- Retains downloaded Trip content and previously viewed map tiles for offline use
- Real-time updates via Server-Sent Events (SSE)

---

## Getting Started

### Connect to Your Server

1. Sign in to your Wayfarer web app and create an **API token** under your account settings.
2. Open **Settings** in the mobile app.
3. Use the **QR scanner** to prefill Server URL and API Token, or enter your installation's public HTTPS URL and token manually.
4. **Test connection** — the app fetches server settings and activity types.

Keep the token private. If it is revoked or your account is inactive, contact your administrator before retrying.

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

See [Groups](05-Groups.md) for membership and visibility rules.

---

## Trip Navigation

- View your **saved trips** with regions, places, and segments.
- Navigate to places using device navigation apps.
- Download Trip metadata, Places, Segments, Areas, and saved geometry for **offline access**.
- View trip details including notes and travel modes.

Create and edit ordered intermediate Places in the [web Trip Editor](04-Trips.md#segments-routes). Mobile 1.3.0 retains valid ordered intermediate Places online and in downloaded Trips, and shows Start/Via/End details. Missing or malformed waypoint information is shown as unavailable. Older clients may show only endpoints and effective route geometry. Mobile notes-only updates do not replace or erase server-owned waypoint data.

Waypoint authoring remains web-first. For guidance to a Place or a dropped pin, use **Directions**: saved Segment geometry takes priority; other choices include retained Wayfarer routes, a fresh server-hosted route, Direct guidance, or an external maps app.

---

## Offline Maps

### Tile Caching

- Trip downloads store geographic content, not a raster basemap package.
- The interactive map caches OpenStreetMap tiles as you pan and zoom. Previously viewed tiles can be used while they remain in the bounded cache.
- Set the live cache size limit, inspect usage, or clear cached tiles under **Settings > Map Cache**. Clearing it does not remove downloaded Trips.
- Cached tiles do not guarantee complete offline coverage. A blank offline basemap can coexist with usable downloaded Places and routes.

### Tile Server Configuration

The current mobile basemap requests OpenStreetMap tiles directly and caches them locally. This is separate from the web application's server-side tile provider and cache settings. Respect the tile provider's usage policy.

## Routing and Offline Guidance

- Hosted routing is available in Mobile 1.3.0 with a compatible backend. Configure and verify a personal directions provider on Wayfarer, then choose an offered provider mode in Directions. Provider credentials stay on the server. See [Personal Location Providers](24-Personal-Location-Providers.md#geoapify-persistent-geocoding-and-routing).
- Provider modes are separate from Segment Transport Profiles. Only supported modes can request a route; unknown choices make no provider contact. The server retains compatibility for older clients using exact built-in profile keys (`walk`, `bicycle`, `bike`, `car`, or `bus`), without accepting arbitrary labels.
- Saved Segment geometry remains authoritative. When it is unavailable, matching validated persistent Wayfarer guidance may be retained locally and reused offline. You can explicitly request a fresh Wayfarer route; failure or cancellation preserves the prior retained route.
- **Direct** is always an explicit, network-free choice for straight-line distance and bearing. It is not a calculated road route. Public OSRM and direct routing-provider fallback are not used.
- Unavailable routing affects navigation only: authentication and synchronization remain usable, along with saved Segment geometry and Direct guidance.

---

## Real-Time Updates (SSE)

The app subscribes to Server-Sent Events for instant updates:

- **Location updates** — see group member movements
- **Visit notifications** — when you arrive at planned places
- **Invitation notifications** — new group invitations
- **Membership changes** — group member updates

The current WayfarerMobile source reconnects automatically after a remote stream ends or a non-cancelled body read fails. Group and visit subscriptions use the same retry delays: 1 second, 2 seconds, then 5 seconds. Transient HTTP failures, including 429/503 admission responses, also retry. Explicit Stop or local cancellation ends the subscription; 401/403/404 responses require correcting authentication, access, or the endpoint.

The published [Android 1.3.0 build](https://github.com/stef-k/WayfarerMobile/releases/tag/1.3.0) retries transient HTTP failures but does not include remote stream-end/body-read recovery. If you use that build and live updates stop, reconnect through the app or restart it. Check [releases](https://github.com/stef-k/WayfarerMobile/releases) for a build containing the current source behavior.

---

## Privacy and Security

- **You control your history** — Wayfarer's authoritative data and history are stored on your server; the app also keeps local downloaded content, history, and pending work.
- **External services** — optional geocoding and routing providers receive the request information needed for configured features. Map tile services receive tile requests, and opening an external navigation app passes the selected destination to that app.
- **Provider credentials** remain server-side and are never sent to WayfarerMobile. Hosted routing goes through Wayfarer; see [provider privacy boundaries](24-Personal-Location-Providers.md#resumable-workflow-authority).
- **API tokens** provide secure authentication.
- Rotate tokens if exposed or compromised.
- Review the configured server URL before entering a token.

---

## Settings

| Setting | Description |
|---------|-------------|
| Server URL | Your Wayfarer instance address |
| API Token | Authentication token from web app |
| Tracking Enabled | Toggle background location logging |
| Map Cache | Set the size limit, inspect usage, or clear previously viewed tiles |

---

## Troubleshooting

### Connection Issues

- Verify server URL is correct and accessible.
- Check API token is valid (create a new one if needed).
- Ensure device has network connectivity.
- See [server health and proxy troubleshooting](09-Troubleshooting.md) if the same URL also fails in a browser.

### Location Not Updating

- Check tracking is enabled in app settings.
- Verify location permissions are granted.
- Check battery optimization isn't killing background services.

### Tiles Not Loading

- Check internet access to OpenStreetMap, or whether the needed tiles were previously viewed and remain cached.
- If the cache is corrupted, clear it in Map Cache settings, then reconnect and view the affected area to refill it.
- Trip downloads alone do not download basemap tiles; downloaded Trip content remains separate from the live cache.

### Offline Queue or Missing History

The pending delivery queue, phone Timeline, and server history are separate. Exporting or importing history does not clear the queue. Use [offline recovery and reconciliation](07-Importing-Exporting.md#offline-queue-recovery), and verify imported history before resuming delivery. Do not uninstall or clear app data to repair synchronization.


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
