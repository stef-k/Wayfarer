# Trips

Overview
- A Trip contains Regions. Regions contain Places and Areas. Trips can also have Segments (routes) that connect Places.
- Trips are private by default; you can make them public to share.

Creating a Trip
1) Open Trips and click New.
2) Name your trip and save. You can toggle public/private at any time.

Regions and Places
- Add a Region to organize related places. Add Places as points with coordinates and notes.
- Areas (polygons) highlight zones; draw them on the map.

Segments (Routes)
- Add Segments between Places. Choose travel mode and add notes and route geometry if available.

Exporting a Trip
- **PDF Guide** — printable trip guide with interactive features:
  - Place names link to Google search
  - Coordinates link to Google Maps
  - Includes map snapshots and complete trip details
  - Cancel export at any time during generation
- **KML** — Wayfarer or Google MyMaps compatible formats.

Automatic Visit Detection
- When you receive GPS pings (from the mobile app or API), the system checks if you're near any planned places in your trips.
- After two consecutive pings within a configurable radius, a **visit event** is recorded automatically.
- Visit events capture arrival time, departure time, and a snapshot of the place details (name, location, notes) at the time of visit.
- This works with all location sources: mobile app tracking, manual check-ins, and web app location entries.
- Settings like detection radius, accuracy thresholds, and confirmation requirements can be adjusted in Admin > Settings.

Tips
- Keep names concise for cleaner export filenames.
- Use Areas for boundaries and Segments for movement between Places.

