# Trips

Trips are the core trip planning feature in Wayfarer, allowing you to organize destinations, routes, and detailed travel information.

---

## Trip Structure

A Trip contains a hierarchy of elements:

- **Regions** — logical groupings of places (e.g., cities, areas of interest)
- **Places** — specific points of interest with coordinates and notes
- **Areas** — polygonal zones drawn on the map
- **Segments** — routes connecting places with travel mode information

Trips are **private by default**; you can make them public to share with others.

---

## Creating a Trip

1. Open **Trips** and click **New**.
2. Name your trip and save.
3. Toggle **public/private** at any time.
4. Add a **cover image** for visual identification in trip lists.

---

## Regions and Places

- Add a **Region** to organize related places.
- Add **Places** as points with:
  - Coordinates (lat/lon)
  - Notes (rich HTML supported)
  - Icon and marker color
  - Travel mode
- **Areas** (polygons) highlight zones; draw them directly on the map.

---

## Segments (Routes)

- Add **Segments** between Places.
- Choose **travel mode** (walking, driving, transit, etc.).
- Add notes and route geometry if available.
- Segments display as connected lines on the map.

---

## Trip Tags

- Add **tags** to trips for organization and discovery.
- Tags use case-insensitive matching.
- Public trips can be browsed by tag.
- Manage tags from the trip edit page.

---

## Trip Thumbnails

- Trips automatically generate **map thumbnail previews**.
- Thumbnails appear in trip lists and cards.
- Generated using Playwright browser automation.
- Thumbnails update when trip content changes.

---

## Importing Trips

Import trips from external sources:

- **Google MyMaps KML** — import your Google MyMaps designs directly.
- **Wayfarer KML** — reimport trips exported from Wayfarer.
- Duplicate detection with configurable handling modes.
- Imports preserve regions, places, areas, and segments.

---

## Exporting Trips

### PDF Guide

Printable trip guide with interactive features:

- **Clickable place names** — link to Google search
- **Clickable coordinates** — link to Google Maps
- Map snapshots for overview, regions, places, and segments
- Complete trip details including notes, travel modes, distances
- **Cancel export** at any time during generation
- SSE progress updates during generation

### KML Export

- **Wayfarer format** — preserves all metadata for reimport
- **Google MyMaps format** — compatible with Google MyMaps

---

## Automatic Visit Detection

When you receive GPS pings (from the mobile app, API, or manual entries), the system automatically detects visits to your planned places.

### How It Works

1. GPS ping arrives within configured radius of a trip place.
2. **Two-hit confirmation** — a second ping confirms the visit (reduces GPS noise false positives).
3. A **PlaceVisitEvent** is recorded with:
   - Arrival time (UTC)
   - Departure time (when you leave or timeout)
   - Place snapshot (name, location, notes preserved even if place deleted)
   - Trip and region context

### Visit Management

- View visit history from **User > Visits**.
- Search by place name, region name, date range.
- Edit or delete individual visits.
- Visit data persists independently of trip changes.

### Configuration

Adjust in **Admin > Settings**:

- Detection radius (meters)
- Accuracy thresholds
- Confirmation window (derived from location threshold)
- End-visit timeout

---

## Public Trip Sharing

- Make trips public to share via URL.
- Public trips display **visit progress** showing which places you've visited.
- Viewers see your journey progress in real-time via SSE updates.

---

## Tips

- Keep names concise for cleaner export filenames.
- Use **Areas** for boundaries and **Segments** for movement between Places.
- Add cover images for visual organization.
- Use tags to group related trips.

