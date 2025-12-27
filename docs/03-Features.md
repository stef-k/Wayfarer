# Features Overview

Maps and Tiles
- Interactive map via Leaflet with OpenStreetMap tiles.
- Local tile caching (when enabled) to improve speed and reduce bandwidth. Tiles for zoom levels 0-8 are cached permanently; higher zooms use an LRU policy with an admin-configurable default cap of 1024 MB.

Timeline
- Visualize your location history over time.
- Filter by time range or activity type.
- Reverse geocoding enriches points with human‑readable addresses when a personal token is configured.

Trips
- Organize a trip into Regions, Places (points), Areas (polygons), and Segments (routes).
- Add notes, colors, and travel modes. Export to PDF or KML.
- **Automatic visit detection** — when GPS pings arrive near a planned place, the system records a visit event with timestamps and place snapshot data.

Groups
- Share live or recent locations among trusted members. Joining is by invitation.

Privacy
- Self‑hosted; you control where data lives and who can access it. Public sharing is opt‑in.
