# Database

ORM & Provider
- EF Core with Npgsql provider and NetTopologySuite for spatial types.
- PostGIS is required (e.g., `geography(Point, 4326)` for `Location.Coordinates`).

Key Entities (selected)
- `ApplicationUser` — identity user, profile flags (IsActive, IsProtected).
- `Location` — point with timestamp, optional reverse-geocoded fields, activity type metadata.
- `Trip`, `Region`, `Place`, `Area`, `Segment` — trip planning model; cascading deletes and timestamp stamping on `Trip.UpdatedAt`.
- `PlaceVisitEvent`, `PlaceVisitCandidate` — visit detection and tracking.
- `Group`, `GroupMember`, `GroupInvitation` — group ownership, membership, invitations, visibility flags.
- `ApiToken` — per‑user tokens for API access.
- `ApplicationSettings` — admin‑editable runtime settings stored in DB.
- `AuditLog`, `JobHistory`, `TileCacheMetadata`, `LocationImport` — diagnostics, jobs, cache, and import tracking.

---

## Core Data Models

### Location

Represents a single GPS point in a user's timeline.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | int | Primary key |
| `UserId` | string | Owner (FK to ApplicationUser) |
| `Coordinates` | Point (SRID 4326) | GPS coordinates (longitude, latitude) |
| `LocalTimestamp` | DateTime | Timestamp (stored as UTC) |
| `TimeZoneId` | string | IANA timezone identifier |
| `ActivityTypeId` | int? | Activity type (walking, driving, etc.) |
| `Notes` | string | User notes |
| `Accuracy` | double? | GPS accuracy in meters |
| `Altitude` | double? | Elevation above sea level |
| `Speed` | double? | Movement speed |
| `Heading` | double? | Compass bearing (0-360) |
| `Source` | string | Origin identifier (mobile, import, api) |
| `Address`, `Country`, `Region`, `Place`, `PostCode` | string | Reverse-geocoded address fields |
| `FullAddress` | string | Complete formatted address |

---

### Trip

Container for trip planning with regions, places, areas, and segments.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | Guid | Primary key |
| `UserId` | string | Owner |
| `Name` | string | Trip name |
| `Notes` | string | Rich-text HTML description |
| `IsPublic` | bool | Whether trip is publicly visible |
| `ShareProgressEnabled` | bool | Share visit progress with public viewers |
| `CenterLat`, `CenterLon` | double? | Map center for permalinks |
| `CenterZoom` | int? | Map zoom level for permalinks |
| `CoverImageUrl` | string | Optional cover image |
| `CreatedAt`, `UpdatedAt` | DateTime | Timestamps |

**Collections:** Regions, Segments, Tags

---

### Region

Geographic grouping within a trip containing places and areas.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | Guid | Primary key |
| `UserId` | string | Owner |
| `TripId` | Guid | Parent trip |
| `Name` | string | Region name |
| `Center` | Point | Geographic center |
| `Notes` | string | Rich-text HTML notes |
| `DisplayOrder` | int | Sort order within trip |
| `CoverImageUrl` | string | Optional cover image |

**Collections:** Places, Areas

---

### Place

Point of interest within a region.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | Guid | Primary key |
| `UserId` | string | Owner |
| `RegionId` | Guid | Parent region |
| `Name` | string | Place name |
| `Location` | Point | GPS coordinates |
| `Notes` | string | Rich-text HTML notes |
| `DisplayOrder` | int | Sort order within region |
| `IconName` | string | Map icon identifier |
| `MarkerColor` | string | Hex color for marker |
| `Address` | string | Address text |

---

### Area

Polygonal zone within a region (e.g., neighborhoods, parks).

| Field | Type | Description |
|-------|------|-------------|
| `Id` | Guid | Primary key |
| `RegionId` | Guid | Parent region |
| `Name` | string | Area name |
| `Notes` | string | Rich-text HTML notes |
| `DisplayOrder` | int | Sort order within region |
| `FillHex` | string | Fill color (hex) |
| `Geometry` | Polygon | Area boundary |

---

### Segment

Route between two places with travel mode and geometry.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | Guid | Primary key |
| `UserId` | string | Owner |
| `TripId` | Guid | Parent trip |
| `FromPlaceId` | Guid | Starting place |
| `ToPlaceId` | Guid | Destination place |
| `Mode` | string | Travel mode (walk, bicycle, car, transit, etc.) |
| `RouteGeometry` | LineString | Route path |
| `EstimatedDuration` | TimeSpan? | Travel time estimate |
| `EstimatedDistanceKm` | double? | Distance in kilometers |
| `DisplayOrder` | int | Sort order within trip |
| `Notes` | string | Rich-text HTML notes |

**Speed Defaults:** walk: 5 km/h, bicycle: 15 km/h, car: 60 km/h, transit: 40 km/h

---

### PlaceVisitEvent (Visit)

Confirmed visit to a planned trip place.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | Guid | Primary key |
| `UserId` | string | Visitor |
| `PlaceId` | Guid? | Visited place (nullable for deletion survival) |
| `ArrivedAtUtc` | DateTime | First detection timestamp |
| `LastSeenAtUtc` | DateTime | Most recent ping timestamp |
| `EndedAtUtc` | DateTime? | Visit end (null while open) |
| `Source` | string | How visit was created: `realtime`, `backfill`, `backfill-user-confirmed`, `manual` |

**Snapshot Fields** (preserved after trip/place deletion):
- `TripIdSnapshot`, `TripNameSnapshot` — Trip reference
- `RegionNameSnapshot` — Region name
- `PlaceNameSnapshot` — Place name
- `PlaceLocationSnapshot` — Place coordinates
- `IconNameSnapshot`, `MarkerColorSnapshot` — Visual settings
- `NotesHtml` — Per-visit notes (seeded from place notes)

**Computed:**
- `ObservedDwellMinutes` — Time spent at place
- `IsOpen` — Whether visit is still active

---

### PlaceVisitCandidate

Ephemeral record tracking pre-confirmation hits for visit detection.

| Field | Type | Description |
|-------|------|-------------|
| `Id` | Guid | Primary key |
| `UserId` | string | User being tracked |
| `PlaceId` | Guid | Place being monitored |
| `FirstHitUtc` | DateTime | First ping within radius |
| `LastHitUtc` | DateTime | Most recent ping |
| `ConsecutiveHits` | int | Hit count toward confirmation |

Candidates are deleted once a PlaceVisitEvent is created or when stale.

---

## Spatial & Indices
- `Location.Coordinates` uses `geography(Point, 4326)` with GiST index for spatial queries.
- `PlaceVisitEvents` has unique index on `(UserId, PlaceId, Date)` to prevent duplicate visits at the database level.
- Common PostGIS helpers used via NetTopologySuite:
  - `ST_DWithin` ⇒ `geometry.Distance(otherPoint) <= radiusMeters` to filter points near a location.
  - `ST_Intersects` ⇒ `geometry.Intersects(polygon)` to find points intersecting a polygon.
  - `ST_Contains` ⇒ `polygon.Contains(geometry)` for "point inside polygon" checks (e.g., hidden areas).

---

## Seeding

- `ApplicationDbContextSeed` seeds roles, a protected admin account (change credentials immediately), default activity types, and initial settings.

---

## Hidden Areas

- `HiddenArea` polygons are used to filter public timeline results; any location within a user's hidden polygons is excluded from public feeds.

---

## Quartz

- Quartz uses a persistent ADO store with `qrtz_*` tables, auto‑created via `QuartzSchemaInstaller` on startup.
