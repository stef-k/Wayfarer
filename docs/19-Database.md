# Database

ORM & Provider
- EF Core with Npgsql provider and NetTopologySuite for spatial types.
- PostGIS is required (e.g., `geography(Point, 4326)` for `Location.Coordinates`).

Key Entities (selected)
- `ApplicationUser` — identity user, profile flags (IsActive, IsProtected).
- `Location` - point with timestamp, optional reverse-geocoded fields, activity type metadata.
- `Trip`, `Region`, `Place`, `Area`, `Segment` — trip planning model; cascading deletes and timestamp stamping on `Trip.UpdatedAt`.
- `Group`, `GroupMember`, `GroupInvitation` — group ownership, membership, invitations, visibility flags.
- `ApiToken` — per‑user tokens for API access.
- `ApplicationSettings` — admin‑editable runtime settings stored in DB.
- `AuditLog`, `JobHistory`, `TileCacheMetadata`, `LocationImport` — diagnostics, jobs, cache, and import tracking.

Spatial & Indices
- `Location.Coordinates` uses `geography(Point, 4326)` with GiST index for spatial queries.
- `PlaceVisitEvents` has unique index on `(UserId, PlaceId, Date)` to prevent duplicate visits at the database level.
- Common PostGIS helpers used via NetTopologySuite:
  - `ST_DWithin` ⇒ `geometry.Distance(otherPoint) <= radiusMeters` to filter points near a location.
  - `ST_Intersects` ⇒ `geometry.Intersects(polygon)` to find points intersecting a polygon.
  - `ST_Contains` ⇒ `polygon.Contains(geometry)` for "point inside polygon" checks (e.g., hidden areas).

Seeding
- `ApplicationDbContextSeed` seeds roles, a protected admin account (change credentials immediately), default activity types, and initial settings.

Hidden Areas
- `HiddenArea` polygons are used to filter public timeline results; any location within a user’s hidden polygons is excluded from public feeds.

Quartz
- Quartz uses a persistent ADO store with `qrtz_*` tables, auto‑created via `QuartzSchemaInstaller` on startup.
