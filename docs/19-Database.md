# Database

## Location recovery identity

Locations are unique by authenticated `(UserId, IdempotencyKey)`, so the same GUID remains independent across users and concurrent import/drain paths reuse one winner. Keyless legacy data retains timestamp/coordinate compatibility. Counts and proximity are never authority to delete a mobile queue.

ORM & Provider
- EF Core with Npgsql provider and NetTopologySuite for spatial types.
- Personal provider profiles, independent selections, Geoapify rolling admissions, and separate Mapbox product meters use constrained PostgreSQL authority; schema and retention are described in [Personal Location Providers](24-Personal-Location-Providers.md).
- Mapbox Permanent consent is versioned, UTC-timestamped, and credential-generation-bound. Nullable provider/storage-mode/time provenance on `Location` and `Place` remains unknown for historical and manual/imported values; migrations perform no historical rewrite.
- Accepted Geoapify Segment routes use additive nullable normalized instruction, provider/configuration/profile/mapping, generation-time, attribution, and storage-authority columns. Historical geometry is not rewritten and raw provider responses, credentials, and authenticated URLs are never stored.
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
- `LocationEnrichmentWorkflow` is unique by user and uses PostgreSQL `xmin`; `LocationEnrichmentAttempt` is unique by user/Location with a matching-owner composite foreign key. They retain bounded scheduling metadata only.

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

### SegmentWaypoint

Ordered intermediate saved-place anchors are persisted as aggregate children rather than embedded
identifiers in route coordinates.

| Field | Type | Description |
|-------|------|-------------|
| `SegmentId` | Guid | Owning segment; part of the composite primary key |
| `PlaceId` | Guid | Referenced canonical saved place; part of the composite primary key |
| `Position` | int | Zero-based contiguous waypoint order |
| `RouteVertexIndex` | int? | Zero-based interior vertex in custom geometry; null for fallback geometry |

Deleting a Segment cascades to its waypoint associations. The Place relationship is restrictive:
an association never owns or deletes its saved Place. PostgreSQL enforces nonnegative positions,
positive non-null route indices, unique places and positions per Segment, and unique non-null route
indices per Segment. Same-trip ownership, contiguity, endpoint eligibility, saved-place locations,
and coordinate matching remain server aggregate invariants because they cross rows or spatial values.

`SegmentRouteReconciler` is the single route-aggregate persistence boundary. Its internal proposal
contains only Segment and Place identities, waypoint scalars, and optional geometry. The operation
requires a clean caller `DbContext`: after change detection, any pre-existing Added, Modified, or Deleted
entry is rejected before transaction creation, locking, loading, or mutation and remains caller-owned.

For PostgreSQL, the reconciler begins its owned transaction and acquires `SELECT ... FOR UPDATE` on the
canonical Segment row before loading mutable aggregate state. It then refreshes any unchanged tracked
identity-map values and loads canonical endpoints, waypoint rows and Places, Region ownership, and Trip
ownership under that lock. The lock is per Segment and remains held through deletion, final
`SaveChangesAsync`, and commit, so a later reconciliation reloads after the earlier complete proposal
commits. Different Segments are not serialized by an application-global lock.

After validation, the reconciler replaces the complete waypoint association set inside the same
transaction, avoiding PostgreSQL's immediate unique-index collisions during arbitrary reorder while
preserving the final indexes and checks. Rollback and mandatory tracker repair use a non-cancelled cleanup
path and restore only reconciliation-owned Segment, waypoint, and Trip timestamp state. If rollback or
repair fails, the caller context is disposed and the resulting aggregate exception retains both the
original and cleanup failures; an unsafe context is never presented as reusable. Segment has no
optimistic-concurrency token in this slice: the later locked writer may win with one complete revalidated
proposal, but no user-facing conflict semantics are claimed.

Null `RouteGeometry` remains null; fallback consumers use the effective anchor chain
`From → waypoints by Position → To` without persisting a convenience line. A proposed custom LineString
is defensively copied before validation, and only that validated copy is stored. Custom routes use SRID
4326 longitude/latitude coordinates and require every semantic anchor to match within `0.0000001`
degrees independently on each axis. A closed loop reuses one canonical Place for both endpoints and
never creates a duplicate Place or marker identity.

Transport modes resolve through the administrator-managed `TransportProfiles` catalog. The durable segment `Mode` string remains compatible with public and interchange contracts; planning speeds are database configuration rather than documentation constants.

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

- Quartz uses a persistent ADO store with `qrtz_*` tables. Before Quartz initializes, `QuartzSchemaInstaller`
  creates or aligns Wayfarer's owned schema to the pinned Quartz 3.19.1 PostgreSQL definitions.
- The application database role requires `CREATE`, `ALTER`, and `UPDATE` privileges for that schema. Startup
  fails when an existing required Quartz column has an incompatible definition instead of rewriting it.
- Manual installation must use the aligned embedded `Scripts/tables_postgres.sql` script or catalog-equivalent
  definitions, including all Quartz 3.19.1 optional columns owned by the installer.
- Enrichment retains one durable job and one epoch trigger per active workflow. Rollback removes only workflow/attempt scheduling metadata after stopping the scheduler; it never removes Locations, enrichment, provenance, credentials, admissions, or meters.
# Segment measurement provenance

`Segments.EstimatedDurationSource` is a required integer enum: `Automatic = 0` and `Manual = 1`. The database default is Automatic and `CK_Segments_EstimatedDurationSource` rejects every other value. The issue 405 migration classifies each legacy non-null duration as Manual and each null duration as Automatic without recalculating distance or duration. Downgrading removes the column and permanently loses provenance written after the upgrade; re-upgrading can only reclassify from duration nullability.

Distance is never accepted from a writer. The server uses custom SRID 4326 `RouteGeometry` when present, otherwise the complete ordered anchor fallback. It sums Haversine distance over consecutive `[longitude, latitude]` coordinates with radius `6,371,000 m`, retains metres transiently for duration, and stores kilometres rounded to three decimals away from zero. Automatic duration uses the linked database planning speed and rounds seconds away from zero. Manual duration is required, non-negative, and normalized to whole seconds.

Single-Segment mutations lock current/proposed transport profiles by ascending GUID and then the Segment. Profile-speed mutations use a serializable transaction, lock the profile and affected Segments in ascending GUID order, recompute Automatic measurements, preserve Manual durations, and commit profile, Segments, and bounded audit data atomically.
