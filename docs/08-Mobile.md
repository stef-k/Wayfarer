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
- WayfarerMobile never receives personal routing-provider credentials and contacts only its configured Wayfarer server for hosted routing. Authenticated provider-neutral discovery presents the server's available routing profiles without provider contact or credit use. Catalog identity confirms the displayed chooser state through capability selection; capability then supplies the selected profile's execution authority. A change to that selected authority blocks unsafe route execution or publication, while unrelated catalog presentation changes do not cancel a confirmed route.
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

