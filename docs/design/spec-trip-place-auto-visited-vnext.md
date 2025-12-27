# Wayfarer — Trip Place Auto-Visited (Server-Side) Specification (vNext)

## 1. Summary

Wayfarer already logs a user timeline via `log-location`, but the server (and mobile) apply time/distance thresholds that can skip storing many pings. This feature adds a **server-side** capability to automatically detect when a user has **visited a planned Trip Place**, without changing the mobile request contract and without increasing timeline density.

Key outcomes:
- Places can be auto-marked “visited” based on server-received pings (even if those pings are skipped for timeline storage).
- Revisits are supported via **visit events** (multiple visits to the same place across days/years).
- Historical context is preserved per visit by snapshotting names, coordinates, and notes into the visit event.
- Visit history survives **hard deletes** of trips (via nullable FK + snapshot fields).

---

## 2. Goals and constraints

### 2.1 Goals
- Auto-detect visits to `Place` items that belong to a user’s trips.
- Support revisits and per-visit “observed dwell” time windows.
- Preserve historical reference even if the user later edits trip/place metadata.
- Keep the existing timeline logic and storage volume unchanged.

### 2.2 Constraints
- **No breaking changes** to mobile request/response for `/log-location`.
- Existing timeline threshold behavior (time/distance filtering) remains unchanged.
- Settings must use the existing **DB-backed settings table** and **in-memory cache** invalidation/revalidation pattern.
- Must be efficient (PostGIS indexed proximity query; avoid full scans).

### 2.3 Non-goals (v1)
- Full “trip execution sessions” / run grouping (can be added later).
- Client-side geofencing or adaptive polling strategies.
- Push notifications (possible later).
- UI work beyond Admin settings (user-facing UI is future work).

---

## 3. Data model changes (trip-related + durable history)

### 3.1 New table: `PlaceVisitEvent` (source of truth)

Purpose: store each actual visit to a place as a separate record (supports revisits). The event row is designed to remain useful even if the trip plan is hard-deleted.

**Entity: `PlaceVisitEvent`**
- `Id` (`Guid`)
- `UserId` (`string`) — Identity user key

**Optional link to live plan (when it exists):**
- `PlaceId` (`Guid?`, nullable) — FK to `Places.Id`
  - **FK delete behavior:** `ON DELETE SET NULL`
  - This keeps history rows even if a Trip hard delete removes Places.

**Visit lifecycle (UTC):**
- `ArrivedAtUtc` (`DateTime`) — set from candidate `FirstHitUtc`
- `LastSeenAtUtc` (`DateTime`) — last server ping inside radius
- `EndedAtUtc` (`DateTime?`) — null while “open”; set when the visit is considered ended

**Snapshots (for durability + historical accuracy):**
- `TripIdSnapshot` (`Guid`) *(not FK)*
- `TripNameSnapshot` (`string`)
- `RegionNameSnapshot` (`string`)
- `PlaceNameSnapshot` (`string`)
- `PlaceLocationSnapshot` (`Point`) — PostGIS Point (NetTopologySuite), SRID preserved (typically 4326)

**Notes (single field; seeded automatically, editable later):**
- `NotesHtml` (`string?`)
  - On event creation: copy from `Place.Notes` (HTML rich text; image URLs only)
  - Later: user edits this field for that visit (future UI)

Optional UI snapshot fields (not required):
- `IconNameSnapshot` (`string?`)
- `MarkerColorSnapshot` (`string?`)

**Indexes**
- `(UserId, EndedAtUtc)` (fast lookup of open visits for user)
- `PlaceId` (nullable)
- `ArrivedAtUtc` (reporting by date range)

### 3.2 New table: `PlaceVisitCandidate` (pre-confirmation hit tracking)

Purpose: confirm a visit via multiple inside-radius pings without relying on timeline rows.

**Entity: `PlaceVisitCandidate`**
- `Id` (`Guid`)
- `UserId` (`string`)
- `PlaceId` (`Guid`)
- `FirstHitUtc` (`DateTime`)
- `LastHitUtc` (`DateTime`)
- `ConsecutiveHits` (`int`)

**Constraints/Indexes**
- Unique index on `(UserId, PlaceId)`
- Index on `LastHitUtc` (cleanup)
- FK to `Places(PlaceId)` (delete behavior can cascade; candidates are ephemeral)

---

## 4. Server processing flow

### 4.1 Where to call it (do not break existing behavior)

In the server `log-location` endpoint (the same one used by mobile for timeline logging), call visit detection **before** the existing timeline threshold checks / early returns.

Conceptual flow:
1. Receive ping
2. **Process visit detection** (may create/update/close events)
3. Run existing timeline threshold logic (store or skip timeline row)
4. Return existing response contract (`Success=true`, `Skipped=true/false`)

Visit detection should not change what the mobile client expects from `log-location`.

### 4.2 Effective radius and accuracy handling

Given a ping with `Location` (Point) and optional `AccuracyMeters`:

- If `AccuracyMeters > VisitedAccuracyRejectMeters` and reject is enabled:
  - Skip visit detection for this ping (but do not fail the request)
- Compute effective radius:
  - `radius = clamp(max(VisitedMinRadiusMeters, AccuracyMeters * VisitedAccuracyMultiplier), VisitedMinRadiusMeters, VisitedMaxRadiusMeters)`

### 4.3 Candidate places query (PostGIS)

Query places belonging to the user that are within `VisitedMaxSearchRadiusMeters` of the ping location.

Implementation guidance:
- Use PostGIS `ST_DWithin` on `geography` to query by meters (or cast geometry->geography).
- Ensure a GiST index exists on `Place.Location`.
- Filter to only the user’s places (via Trip ownership rules as currently implemented).

**Important behavior to avoid accidental multi-visits in dense areas:**
- Process **only the nearest** place within the effective radius for that ping.
- If none within effective radius, do not create/update events.

(This keeps v1 conservative. Future work can add multi-place disambiguation.)

### 4.4 Two-hit confirmation + event lifecycle

Given the nearest place `place` where `distance <= radius`:

#### A) If an open event exists
Find open event:
- `PlaceVisitEvent` where `UserId == userId && PlaceId == place.Id && EndedAtUtc == null`

Then:
- `LastSeenAtUtc = now`
- Save changes
- Delete candidate row if it exists (defensive cleanup)

#### B) If no open event exists (confirm via candidate)
Load candidate row `(userId, placeId)`:

- If no candidate row:
  - create candidate with `ConsecutiveHits=1`, `FirstHitUtc=now`, `LastHitUtc=now`
- Else if `(now - candidate.LastHitUtc) <= VisitedHitWindowMinutes`:
  - `ConsecutiveHits += 1`
  - `LastHitUtc = now`
- Else:
  - reset: `ConsecutiveHits=1`, `FirstHitUtc=now`, `LastHitUtc=now`

If `ConsecutiveHits >= VisitedRequiredHits`:
- Create `PlaceVisitEvent`:
  - `UserId = userId`
  - `PlaceId = place.Id`
  - `ArrivedAtUtc = candidate.FirstHitUtc`
  - `LastSeenAtUtc = now`
  - `EndedAtUtc = null`
  - Populate snapshots from current plan entities:
    - `TripIdSnapshot = trip.Id`
    - `TripNameSnapshot = trip.Name`
    - `RegionNameSnapshot = region.Name`
    - `PlaceNameSnapshot = place.Name`
    - `PlaceLocationSnapshot = copy of place.Location` (preserve SRID)
  - Seed notes:
    - `NotesHtml = place.Notes` (copy) enforcing `VisitedPlaceNotesSnapshotMaxHtmlChars` (see §6)
- Delete candidate row
- Save changes

#### Outside-radius pings
If the nearest place is not within `radius`, or no place is found:
- Do nothing (v1)
- Cleanup stale candidates separately (see §4.6)

### 4.5 Closing visits (setting `EndedAtUtc`)

A visit is considered ended if the server stops receiving inside-radius pings for a configurable time.

On each `ProcessPingAsync(...)` call (or via a periodic job), close stale open events for that user:
- For open events where `now - LastSeenAtUtc > VisitedEndVisitAfterMinutes`:
  - set `EndedAtUtc = LastSeenAtUtc` (end at last observed inside-radius ping)

This produces a clean observed time window:
- Observed dwell: `LastSeenAtUtc - ArrivedAtUtc`

### 4.6 Cleanup (candidates)
Delete stale candidates:
- `LastHitUtc < now - VisitedCandidateStaleMinutes`

---

## 5. Response contract to mobile

No changes required.

`log-location` continues to return the same fields, and continues to set `Skipped=true` when timeline persistence is not performed.

Visit detection runs “side-effect only”:
- It may create/update/close `PlaceVisitEvent` rows.
- The mobile app does not need to know about it during pinging.

---

## 6. Settings (DB-backed + cached)

Wayfarer settings are stored in a DB table and served through an in-memory cache that revalidates when values change. This feature must follow that system.

### 6.1 New setting keys (v1 defaults)
- `VisitedRequiredHits` = 2
- `VisitedHitWindowMinutes` = 15
- `VisitedMinRadiusMeters` = 35
- `VisitedMaxRadiusMeters` = 150
- `VisitedAccuracyMultiplier` = 2.0
- `VisitedAccuracyRejectMeters` = 200 (0 disables)
- `VisitedMaxSearchRadiusMeters` = 200
- `VisitedCandidateStaleMinutes` = 60
- `VisitedEndVisitAfterMinutes` = 45
- `VisitedPlaceNotesSnapshotMaxHtmlChars` = 20000

### 6.2 Notes copy policy (single notes field)
When creating a `PlaceVisitEvent`, copy `Place.Notes` to `PlaceVisitEvent.NotesHtml` enforcing `VisitedPlaceNotesSnapshotMaxHtmlChars`.

Recommended policy:
- **Truncate** to max chars (append `…`) rather than skipping, for best user experience.

---

## 6A. Admin Settings UI (required)

Update the existing Admin Settings screen to include a new section:

**Section title:** `Trip Place Auto-Visited`

Fields (with validation + help text):
- `VisitedRequiredHits` (int, default 2, min 2, max 5)
- `VisitedHitWindowMinutes` (int, default 15, min 5, max 120)
- `VisitedMinRadiusMeters` (int, default 35, min 10, max 200)
- `VisitedMaxRadiusMeters` (int, default 150, min 50, max 500)
- `VisitedAccuracyMultiplier` (decimal/double, default 2.0, min 0.5, max 5.0)
- `VisitedAccuracyRejectMeters` (int, default 200, min 0, max 1000)
- `VisitedCandidateStaleMinutes` (int, default 60, min 15, max 1440)
- `VisitedMaxSearchRadiusMeters` (int, default 200, min 50, max 2000)
- `VisitedEndVisitAfterMinutes` (int, default 45, min 5, max 1440)
- `VisitedPlaceNotesSnapshotMaxHtmlChars` (int, default 20000, min 1000, max 200000)

Implementation notes:
- Use the existing settings controller/service and existing DB-backed settings table.
- On save, update DB values and ensure the in-memory cache invalidates/revalidates immediately (existing behavior).
- Use the existing validation and alert UI patterns; do not add a new settings subsystem.

Validation rules:
- `VisitedMaxRadiusMeters >= VisitedMinRadiusMeters`
- `VisitedMaxSearchRadiusMeters >= VisitedMaxRadiusMeters`
- `VisitedAccuracyRejectMeters == 0` means “disabled”
- Enforce max HTML chars during notes copy

---

## 7. Deletion semantics (hard delete compatibility)

Trips currently use **hard delete**.

This design intentionally keeps visit history even if the trip plan is deleted:
- `PlaceId` is a nullable FK with `ON DELETE SET NULL`
- Snapshot fields (`TripNameSnapshot`, `PlaceLocationSnapshot`, `NotesHtml`, etc.) keep the event meaningful after delete

Recommended future UX (not required for v1):
- “Delete trip plan” (keeps visit history)
- “Delete trip plan + visit history” (purges events where `TripIdSnapshot == tripId`)

---

## 8. Performance and correctness notes

- Use a GiST index on `Place.Location` and query via `ST_DWithin` (meters).
- Always filter places to the user’s data set (ownership/permissions).
- Process only the nearest place within radius for v1 to avoid accidental multi-visits in dense POI clusters.
- Use server `now` timestamps for consistency; treat device timestamps as advisory only if present.
- Ensure `Point` SRID is set consistently (typically 4326). NetTopologySuite uses `X=lon`, `Y=lat`.

---

## 9B. Value unlocked (what this feature enables)

With `PlaceVisitEvent` (revisits supported, visit lifecycle timestamps, durable snapshots, and hard-delete safety), Wayfarer gains a strong “trip execution + history” layer that complements the timeline without increasing timeline DB density.

### 9B.1 Trip execution and progress
- Automatic “done/checked” places while the user travels (a place is visited if it has one or more events).
- Trip and per-region progress derived from visit events.
- Conservative detection (nearest-only) keeps progress trustworthy.

### 9B.2 True revisit history (multi-year)
- Multiple events per place enables clean “visited again” behavior.
- Japan 2026 vs Japan 2027 (same plan reused) becomes natural: separate events, separate notes snapshots.

### 9B.3 Observed dwell per visit
- Each event provides an observed dwell window (`LastSeenAtUtc - ArrivedAtUtc`).
- `EndedAtUtc` provides a clean “visit ended” marker for grouping and reporting.

### 9B.4 Historical durability (plan can be deleted)
- Visit history remains meaningful even if the trip plan is deleted:
  - snapshot names + `PlaceLocationSnapshot` + `NotesHtml` remain
  - nullable FK links back to plan when it exists
- Enables a future “delete plan but keep history” UX without redesign.

### 9B.5 Notes workflow (“edit at home”)
- Notes are automatically seeded from `Place.Notes` at visit creation.
- Later editing of the event notes supports “review and refine after the trip” without changing the trip plan.

### 9B.6 Timeline enrichment (future UI)
- Link timeline segments to places by correlating event time windows.
- Highlight trip days in timeline (days with one or more visit events).
- Jump-to-map using snapshot coordinates even when plan is gone.

### 9B.7 Map UX upgrades (future UI)
- Visited/unvisited marker styling derived from event existence.
- Filters: visited-only/unvisited-only per trip or per region.
- “Visited today” / “recently visited” overlays based on visit timestamps.

### 9B.8 Automation hooks (optional later)
- Emit SSE updates when a visit starts/ends so UI updates live.
- Optional notifications/webhooks (opt-in) on visit creation/closure.

---

## 9A. Future UI additions (not required for v1)

### 9A.1 Trip planning UI (Places list)
- Show visited indicator (derived from visit events count).
- Show latest visit time (`ArrivedAtUtc` / `EndedAtUtc`).
- Allow editing `PlaceVisitEvent.NotesHtml`.

### 9A.2 Map visualization
- Render visited places with a distinct marker style.
- Filter toggle: show only unvisited places.

### 9A.3 Visit history views
- “Visit history” page (by trip, by year, by country).
- Revisit stats and “time at place (observed)” summaries.

### 9A.4 Manual corrections
- Admin/user actions: create/end/delete visit events (audit-friendly).

---

## 10. Implementation checklist (agent)

1. Add EF Core entities:
   - `PlaceVisitEvent` (fields in §3.1)
   - `PlaceVisitCandidate` (fields in §3.2)
2. Create migrations:
   - `PlaceVisitEvent.PlaceId` nullable FK with `ON DELETE SET NULL`
   - PostGIS `Point` column for `PlaceLocationSnapshot` (SRID consistent)
3. Add/extend PostGIS indexes:
   - Ensure GiST index on `Place.Location`
4. Implement `PlaceVisitDetectionService`:
   - Query nearest place within radius (§4.3)
   - Candidate confirmation (§4.4)
   - Event create/update (§4.4)
   - Event closing (§4.5)
   - Candidate cleanup (§4.6)
5. Seed snapshot fields on event creation:
   - Trip/Region/Place snapshot names
   - `PlaceLocationSnapshot` as `Point` copy (preserve SRID)
   - `NotesHtml` = `Place.Notes` truncated to `VisitedPlaceNotesSnapshotMaxHtmlChars`
6. Wire into `log-location`:
   - Call visit detection **before** existing timeline threshold checks
   - Do not change response contract
7. Add settings keys to DB-backed settings system:
   - Defaults in constants; defensive fallbacks if missing
   - Cache invalidation/revalidation on update (existing behavior)
8. Update Admin Settings UI:
   - New “Trip Place Auto-Visited” section (§6A)
   - Validation and help text
9. Add minimal logging:
   - Event created/closed counts (debug-level) to aid verification
10. Verify:
   - Dense city center behavior (nearest-only)
   - Accuracy reject behavior
   - Trip hard delete: events remain; `PlaceId` becomes null; snapshots still display

---

## 11. Guardrails (“do not break existing functionality”)

- Do not change timeline threshold logic or stored record counts.
- Do not change the mobile client payload or expected server response.
- Visit detection must be side-effect-only and must not throw user-facing errors for bad GPS accuracy; it should no-op safely.
- Keep the call site minimal: one service call inserted before the current timeline filter returns.

---

## 12. UI Architecture Reference (for Phase 2: Visit UI)

This section documents the existing UI architecture to guide implementation of visit-related UI features.

### 12.0 Privacy Requirement (Critical)

**Visit data is personal information and must NEVER appear in public-facing views.**

| Context | Visit Data Visible? |
|---------|---------------------|
| User Area (`/User/Trip/Edit`) | ✅ Yes - owner only |
| User Area (`/User/Trip/View`) | ✅ Yes - owner only |
| Public Trip Viewer (`/Trip/Viewer/{id}`) | ❌ No |
| Public Trip API (`/api/trips/{id}` unauthenticated) | ❌ No |
| Mobile API (authenticated, own trips) | ✅ Yes |
| Shared/Group views | ❌ No (unless explicit future opt-in) |

**Implementation rules:**
- Visit queries must filter by `UserId` matching the authenticated user
- Public trip DTOs must NOT include visit counts or indicators
- Map markers in public views use standard styling (no visited state)
- Progress indicators only in authenticated User Area views
- No visit data in public embed/share URLs

### 12.1 Trip Editor Structure

**Route:** `/User/Trip/Edit/{id}`

**Layout:** 3-column responsive design
- **Left sidebar (col-3):** Trip settings panel + Regions/Places accordion + Segments list
- **Right area (col-9):** Search bar + Coordinate inputs + Context banner + Leaflet map

**Key Files:**
| Component | File |
|-----------|------|
| Main view | `Areas/User/Views/Trip/Edit.cshtml` |
| Region item | `Areas/User/Views/Trip/Partials/_RegionItemPartial.cshtml` |
| Place form | `Areas/User/Views/Trip/Partials/_PlaceFormPartial.cshtml` |
| Segment item | `Areas/User/Views/Trip/Partials/_SegmentItemPartial.cshtml` |

**JavaScript Modules** (`wwwroot/js/Areas/User/Trip/`):
| Module | Purpose |
|--------|---------|
| `Edit.js` | Main entry point, initializes map/handlers/store |
| `mapManager.js` | Leaflet integration, marker management |
| `placeHandlers.js` | Place CRUD operations |
| `regionHandlers.js` | Region CRUD operations |
| `store.js` | Central reactive state management |
| `storeInstance.js` | Store singleton |
| `uiCore.js` | UI helper functions |

### 12.2 Current Place Display

**In Region Accordion** (`_RegionItemPartial.cshtml`):
```
┌─────────────────────────────────────────────────────────┐
│ [drag] [icon] Place Name                    [i] [✎] [×] │
└─────────────────────────────────────────────────────────┘
```
- Drag handle for reordering
- Icon from `MarkerColor` + `IconName` combo
- Name (truncated at 80% width)
- Info button (if Notes or Address present)
- Edit button → loads `_PlaceFormPartial`
- Delete button

**Place Data Model** (`Place.cs`):
- `Id`, `UserId`, `RegionId`
- `Name` (string)
- `Location` (NetTopologySuite Point, SRID 4326)
- `Notes` (HTML)
- `DisplayOrder` (int)
- `IconName` (e.g., "museum", "hotel")
- `MarkerColor` (e.g., "bg-red", "bg-blue")
- `Address` (optional, reverse-geocoded)

### 12.3 Map Marker System

**Icon URL Pattern:**
```
/icons/wayfarer-map-icons/dist/png/marker/{bgClass}/{iconName}.png
```

**Marker Creation** (`mapManager.js`):
```javascript
renderPlaceMarker({ id, lat, lon, name, iconName, bgClass })
```

**Marker Storage:**
- `placeMarkersById` — Active place markers (keyed by place ID)
- `regionMarkersById` — Region center markers
- `areaPolygonsById` — Drawn area polygons
- `previewMarker` — Temporary coordinate preview

**Icon Dimensions:** 28×45px, anchor: [14, 45]

**Available Colors:** `bg-red`, `bg-blue`, `bg-green`, `bg-yellow`, `bg-purple`, `bg-orange`, etc.

### 12.4 State Management

**Store Pattern** (`store.js`):
```javascript
const store = {
  state: {
    selectedPlace: null,
    selectedRegion: null,
    selectedSegment: null,
    // ...
  },
  dispatch(action, payload) { /* ... */ },
  subscribe(listener) { /* ... */ }
};
```

**Actions dispatched by handlers:**
- `SELECT_PLACE`, `SELECT_REGION`, `SELECT_SEGMENT`
- `CLEAR_SELECTION`
- Form state updates

### 12.5 Trip Viewer (Read-Only)

**Route:** `/Trip/Viewer/{id}` (public) or `/User/Trip/View/{id}`

**Layout:**
- Collapsible sidebar with trip info, regions, places
- Full-width Leaflet map
- "More actions" dropdown (Edit, Share, Export)

**Key Files:**
- `Views/Trip/Viewer.cshtml`
- `Views/Trip/Partials/_RegionReadonly.cshtml`
- `wwwroot/js/Areas/Public/TripViewer/Index.js`

### 12.6 Timeline Views

**Standard Timeline** (`/User/Timeline`):
- Full-height Leaflet map with clustering
- Marker layers: `markerLayer`, `clusterLayer`, `highlightLayer`
- Live location marker (green) + Latest marker (red)
- SSE stream: `/api/sse/stream/location-update/{username}`

**Chronological Timeline** (`/User/Timeline/Chronological`):
- Period-based navigation (Day/Month/Year)
- Date picker controls
- Stats bar (location count, cities, etc.)

**Key Files:**
- `Areas/User/Views/Timeline/Index.cshtml`
- `Areas/User/Views/Timeline/Chronological.cshtml`
- `wwwroot/js/Areas/User/Timeline/Index.js`
- `wwwroot/js/Areas/User/Timeline/Chronological.js`

### 12.7 API DTOs

**Trip API** (`Models/Dtos/`):
| DTO | Purpose |
|-----|---------|
| `ApiTripDto` | Full trip structure for mobile |
| `ApiTripRegionDto` | Region with places/areas |
| `ApiTripPlaceDto` | Place with `[lon, lat]` coordinates |
| `PlaceCreateRequestDto` | Place creation payload |
| `PlaceUpdateRequestDto` | Place update payload |

### 12.8 SSE Integration

**Current Endpoints:**
- `/api/sse/stream/location-update/{username}` — Location updates
- `/api/sse/group/{groupId}` — Group events (authenticated)

**Event Types:**
- Location updates (new location logged)
- Membership changes (group events)

**JavaScript Handler** (`Timeline/Index.js`):
```javascript
const eventSource = new EventSource(sseUrl);
eventSource.onmessage = (event) => { /* update markers */ };
```

### 12.9 Controllers Reference

**User Area Controllers:**
| Controller | Purpose |
|------------|---------|
| `TripController` | Trip CRUD, clone, export |
| `PlacesController` | Place CRUD, reorder |
| `RegionsController` | Region CRUD, reorder |
| `TimelineController` | Timeline display, settings |
| `LocationController` | Location log + visit detection |
| `SegmentsController` | Travel segments |
| `AreasController` | Drawn polygon areas |

### 12.10 Integration Points for Visit UI

**Phase 1 (MVP) — Where to add visit indicators:**

| Feature | File to Modify | Change | Public View? |
|---------|----------------|--------|--------------|
| Place visited badge | `_RegionItemPartial.cshtml` | Add ✓ badge after place name | ❌ User Area only |
| Map marker styling | `mapManager.js` | Pass `visited` flag, alter icon/style | ❌ User Area only |
| Trip progress count | `Edit.cshtml` | Add "X/Y visited" in trip settings panel | ❌ User Area only |

**Note:** The Trip Viewer (`Views/Trip/Viewer.cshtml`) and its partials (`_RegionReadonly.cshtml`) must NOT show visit data, even for public trips owned by the authenticated user viewing them. Visit indicators are exclusively for the User Area edit/management views.

**Data Flow for Visit Status:**
1. Backend: Query `PlaceVisitEvents` for trip's places
2. ViewModel/DTO: Add `VisitCount` or `IsVisited` per place
3. Razor: Render badge based on visit status
4. JavaScript: Pass visited status to `renderPlaceMarker()`

**SSE Extension for Real-Time:**
| Event | Trigger | Payload |
|-------|---------|---------|
| `visit.started` | New `PlaceVisitEvent` created | `{ visitId, placeId, placeName }` |
| `visit.ended` | `EndedAtUtc` set | `{ visitId, placeId, dwellMinutes }` |
| `trip.progress` | Any visit change | `{ tripId, visited, total }` |

---

## 13. Phase 2 Implementation Checklist (Visit UI)

### Phase 1: Core UI (MVP)
- [ ] Add `VisitCount` to place data in trip edit view
- [ ] Render visited badge (✓) in `_RegionItemPartial.cshtml`
- [ ] Add visited/unvisited marker styling in `mapManager.js`
- [ ] Show trip progress "X/Y places visited" in trip settings panel

### Phase 2: Visit Details
- [ ] Create visit history partial/modal
- [ ] Add per-visit notes editing
- [ ] Display dwell time per visit

### Phase 3: Advanced Features
- [ ] Map filter toggles (visited/unvisited)
- [ ] Progress dashboard with per-region breakdown
- [ ] Timeline integration with visit markers

### Phase 4: Real-Time & Polish
- [ ] SSE events for visit start/end
- [ ] Live progress bar updates
- [ ] Manual correction actions (create/edit/delete visits)
