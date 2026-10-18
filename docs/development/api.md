---
title: API and SSE
---

# API and SSE

Wayfarer's API is implemented by the controllers under `Areas/Api/Controllers/`. It serves the web application, the Vue Trip Editor, integrations and the separate mobile client. Treat the live controllers and DTOs as the contract; this page groups the current surface by responsibility rather than duplicating every action signature.

Development exposes Swagger UI at `/swagger`. Swagger is not enabled by the normal Production middleware path.

## Authentication modes

Wayfarer has three API access patterns:

- **Cookie Identity**: browser requests authenticated by ASP.NET Core Identity. `[Authorize]`/role policies and normal MVC ownership checks apply.
- **API token/bearer**: integration and mobile endpoints resolve the `Authorization` value through the request-scoped `IncomingApiTokenResolver`. Current Wayfarer tokens are generated once for the user, stored as SHA-256 hashes and require an active account.
- **Dual cookie/token endpoints**: selected legacy/general API actions can resolve the active cookie user first and otherwise fall back to an API token. The endpoint still owns resource/role checks.

`api/mobile/*` controllers use the shared incoming token authority, normally via the mobile token accessor, and are bearer/API-token paths. They do not create an ASP.NET authentication principal merely from the token.

### Acquire and replace a connection token

Users obtain the canonical incoming credential through
[Settings → Connect apps](../user/connect-apps.md#get-a-connection-token). This is
a User-role **cookie Identity** surface, with an active database owner, HTTPS and
MVC antiforgery; a bearer header cannot authorize credential issuance. GET shows
only safe metadata. Create returns 201, Replace returns 200, and a stale/competing
state returns 409 without a secret. Replacement checks the observed row ID and
issuance timestamp atomically and invalidates the previous canonical verifier.

The successful JSON contains only `token`, `tokenId` and `issuedAt`. Plaintext is
revealed once after commit, never recovered from storage. Page/response outcomes
are no-store; Done/navigation clears browser token and QR state without revocation.
If the response is lost, refresh safe status and require another explicit replacement;
do not automatically retry credential POSTs. Existing extra administrative tokens
remain separate from this single-credential User workflow.

### Prove the current bearer account

`GET /api/mobile/me` is the additive account-proof contract for successor clients
(#844). Send exactly one `Authorization: Bearer <connection-token>` header to the
verified, trusted HTTPS server origin. The scheme is case-insensitive; cookie
sessions and arbitrary authorization schemes cannot authorize this route. It uses
the same stored hash/legacy incoming-token matching and active-account checks as
existing mobile APIs, with a fresh lookup on each HTTP request.

Success is HTTP 200 with `Content-Type: application/json` and exactly:

~~~json
{ "userId": "<ApplicationUser.Id>" }
~~~

Treat `userId` as an opaque, case-sensitive string scoped to the verified server
origin/instance. Two valid tokens for the same active account return the same ID;
different accounts return different IDs. No username, display name, email, roles,
token value/hash, token metadata or other profile fields are returned. This is a
read-only proof: it does not ingest locations, update last-use counters, rotate
credentials, invoke visits/providers or broadcast SSE. Existing infrastructure
still performs request logging, bounded lookup admission and database reads.

| Outcome | HTTP contract | Consumer interpretation |
| --- | --- | --- |
| Missing, malformed, invalid, revoked token; inactive/deleted account; cookie only | 401 JSON `{"message":"Invalid or missing API token."}`, no login redirect | No bearer account proof |
| Per-IP / global lookup capacity exhausted | 429 / 503 JSON `{"message":"API token lookup capacity exceeded."}`, `Retry-After: 5` | Temporary overload; honor retry hint, do not reject credentials |
| Store/server failure | Existing generic API 500 envelope, including `requestId` | Transient/unproved outcome; do not classify as invalid credentials |
| Cancellation / lost response | No usable proof; the existing API error boundary may emit a generic 500 if still writable | Preserve the candidate as unverified |
| Older backend without this route | Usually 404 (possibly an incompatible response) | Unsupported/unverified candidate, not invalid credentials |

Responses from the action, including authentication, overload and handled lookup
errors, carry `Cache-Control: no-store`. The endpoint adds no token/profile logging;
deployment must retain the existing HTTPS, trusted-proxy/origin and secret-safe
logging configuration. These routed server tests do not establish production
deployment, Android or iOS behavior.

This initial wire contract does not require a new API namespace, application version
or client change in published WayfarerMobile. A successor must require a successful,
well-formed account proof before committing candidate credentials or claiming
same-owner continuity. Offline, unsupported, malformed or otherwise unproved
responses must preserve existing credentials/data and hold or isolate candidate
work. Public Trip success, anonymous settings, URL equality and token hashes are
not identity proof. Account partitions, in-flight fences, atomic credential
replacement and delivery eligibility remain WayfarerApp #5 responsibilities; this
GET never authorizes transferring old work to a new account/server.

## Antiforgery boundary

Browser mutations that rely on cookie Identity use MVC antiforgery, normally through `[ValidateAntiForgeryToken]`. This includes the Trip Editor mutation family and many group/invitation/backfill/browser mutations.

`PUT /api/location/{id}` is the explicit dual-auth boundary: `CookieFirstAntiforgeryAttribute` requires antiforgery whenever an authenticated cookie selected the request, even if a bearer header is also present. Only a successfully resolved bearer request with no selected cookie authority is token-free.

Bearer-only mobile/integration mutations do not require MVC antiforgery. Do not remove antiforgery from a browser mutation merely because a bearer path exists elsewhere.

## Route families

| Family | Current responsibility | Typical authority |
| --- | --- | --- |
| `/api/version` | Application version | anonymous |
| `/api/icons`, `/api/tags`, `/api/settings` | UI/reference data | endpoint/controller rules |
| `GET /api/activity` | shared catalog, ordered by Name, `[{id,name,description}]` | active Identity cookie first, otherwise existing opaque Bearer token; direct 401 on missing/invalid/inactive identity |
| `/api/users` | user lookup, caller/self statistics from cookie Identity claims, authenticated activity/management actions | endpoint rules; statistics use the caller's `NameIdentifier`; cookie roles and ownership for management |
| `/api/trips` | trip reads/search, public trip discovery/images and legacy trip mutations/clone | public where explicitly exposed; otherwise token/cookie ownership |
| `/api/trips/{tripId}/editor` | Vue Trip Editor state, metadata, regions, areas, places, segments, tags, share progress and geocode search | User cookie + ownership; mutations antiforgery |
| `/api/trip-editor/{tripId}/segments/{segmentId}/route-proposals` | external route proposal generation | User cookie + ownership + antiforgery |
| `/api/location` | location ingestion, queries, edits, deletion and stats | token/cookie depending on action; ownership required |
| `/api/visit`, `/api/backfill` | visit queries/mutations and visit backfill | authenticated/owned browser API |
| `/api/groups`, `/api/invitations` | group membership, visibility, location sharing and invitations | authenticated membership/role; browser mutations antiforgery |
| `GET /api/mobile/me` | minimal stable current-account proof, no writes | active bearer/API token only |
| `/api/mobile/groups` | mobile group/member/location/visibility operations | bearer/API token |
| `/api/mobile/visits` | mobile recent-visit recovery | bearer/API token |
| `/api/mobile/routing` | routing profiles, capability and route requests | bearer/API token |
| `/api/sse`, `/api/mobile/sse` | live/reload-hint streams | public eligibility, cookie Identity or bearer according to stream family |

The Trip Editor controller is intentionally split across `TripEditorController.cs` and partial files for areas, places, segments and tag/share-progress behavior. Add an editor endpoint to that cohesive family rather than creating an unrelated controller.

## Trip Editor Place address metadata

`PUT /api/trips/{tripId}/editor/places/{placeId}` accepts a complete editor draft
under the existing User cookie, ownership and antiforgery rules. Without successful
requested reverse geocoding, the submitted manual address and coordinates are
compared with the freshly loaded Place inside the lifecycle transaction. Addresses
are trimmed with null treated as empty; nullable coordinates use exact equality.
Unchanged values preserve feature name/type, provider, storage mode and the original
enrichment timestamp, including Region-only edits. Changing or clearing either
value clears all five metadata fields.

Successful authorized enrichment replaces the address and complete metadata tuple,
including null feature fields, Geoapify `persistent` or Mapbox `permanent` storage
mode and one UTC timestamp reused across internal transaction retries. Unavailable
enrichment saves the manual fallback with the existing `reverse-geocode-unavailable`
warning and applies the same compatibility rule without a fresh timestamp.
Place, metadata, dependent routes, measurements and orders share the lifecycle
commit; a failed save rolls them back. Provider admission remains consumed if
persistence fails. Complete drafts retain last-writer behavior; a lost commit
acknowledgement or response can leave an unknown outcome, so refetch authoritative
state. An HTTP replay may contact the provider again.

## Ownership and role checks

Authentication establishes a caller, not resource access. Controllers/services also enforce trip ownership, group membership/management, target-user constraints and role requirements such as `Admin`, `Manager` or `User`.

For token APIs, resolve the user through the existing request-owned resolver/mobile accessor. Do not re-query raw token values or cache authenticated identities across requests.

## JSON and spatial conventions

Controller JSON is case-insensitive on input and permits named floating-point literals where the configured serializer allows them. API/publication DTOs should still sanitize values before emission.

`PointJsonConverter` writes NetTopologySuite `Point` values as:

~~~json
{ "longitude": 23.72, "latitude": 37.97 }
~~~

The converter does not implement Point deserialization. Input endpoints use their own DTO shapes; do not send arbitrary NTS geometry JSON unless that endpoint explicitly defines it.

Spatial persistence uses longitude as X, latitude as Y and SRID 4326.

## API errors and request IDs

`ApiErrorResponseMiddleware` normalizes terminal `/api` 401, 403 and 404 responses that reach it into JSON with `status`, `error` and `message` while preserving the HTTP status. Unhandled API exceptions become a generic 500 envelope and include `requestId = HttpContext.TraceIdentifier`; the same request ID is placed in Serilog context by `RequestIdLoggingMiddleware`.

Individual actions may return narrower domain errors or compatibility response shapes. Do not assume every non-success response has one universal schema.

API-token lookup admission can return an overload response with `Retry-After: 5`. Location-ingestion admission can return its bounded overload response with `Retry-After: 12`. Clients should honor the status and `Retry-After` instead of tight-loop retrying.

## Location ingestion and idempotency

Send `Content-Type: application/json` and `Authorization: Bearer <connection-token>`
to `POST /api/location/log-location` for automatic capture or
`POST /api/location/check-in` for an intentional check-in. The
[ordinary connection guide](../user/connect-apps.md#use-gpslogger) owns the GPSLogger
configuration; these endpoints accept JSON POST, not GET/query-token logging.

`GpsLoggerLocationDto` accepts:

| Fields | Meaning |
| --- | --- |
| `latitude`, `longitude` | Decimal degrees, ±90/±180; `(0,0)` is rejected. |
| `timestamp` | Capture DateTime; ISO UTC `Z` and numeric offsets preserve the capture instant. No-offset local time is converted using coordinates. Non-nullable DTO defaults are not required-field validation. |
| `accuracy`, `altitude`, `speed` | Optional metres, metres, metres/second respectively. |
| `locationType`, `notes`, `activityTypeId` | Optional capture description, notes and existing activity ID. |
| `source`, `isUserInvoked`, `provider`, `bearing` | Optional origin, invocation flag, sensor/provider and bearing in degrees. |
| `appVersion`, `appBuild`, `deviceModel`, `osVersion`, `batteryLevel`, `isCharging` | Optional capture metadata; battery percentage is 0–100. |

For both check-in and log-location, `2026-08-22T10:00:00+02:00` and
`2026-08-22T08:00:00Z` store capture `LocalTimestamp` as `08:00Z`. An offset-free
`2026-08-22T10:00:00` at Athens coordinates retains coordinate-local interpretation
and stores `07:00Z`; invalid or ambiguous coordinate-local times retain their
existing conversion error. MVC binds numeric offsets to a host-local `DateTime`
representing the same instant; the literal offset text is not retained.
`TimeZoneId` still comes from coordinates, independently of the supplied offset.
`Timestamp` remains server receipt UTC time, separate from capture `LocalTimestamp`.

Omit unavailable measurements. Metadata does not change endpoint filtering:
`isUserInvoked` on log-location is not a check-in bypass. Automatic capture applies
accuracy, time and distance thresholds and duplicate-timestamp rules. A 200 can
be `{ "success": true, "skipped": true, "locationId": null }`, with no new row.
A saved automatic point returns `skipped: false` and its integer `locationId`.
Check-in success returns `message` and the publication-safe `location` object.
Check actual saved history rather than equating HTTP success with persistence.

Anonymous `GET /api/settings` exposes the effective configured time, distance and
accuracy thresholds. Defaults are **5 minutes, 15 m and 50 m** respectively;
configured values take precedence. The real settings service loads persisted row
ID 1 or throws if it is missing; the controller's defensive null-result fallback
is not the shipped accuracy default.

`POST /api/location/check-in` and `POST /api/location/log-location` accept an optional `Idempotency-Key` header containing a GUID. The key is unique per user. A persisted replay returns the prior success before new-work admission; concurrent duplicate persistence is reconciled against the same database invariant.

For a new UUID-keyed create, the Location winner commits before optional personal
provider admission/contact. Only the newly inserted winner attempts immediate
geocoding; a losing insert, persisted replay or failed initial insert consumes no
creation-geocoding admission/contact. Keyless requests retain their existing
enrich-before-insert sequence. Different keys and accounts remain independent.

Successful immediate enrichment saves the complete address/provenance tuple before
the winning response and SSE publication. The short post-HTTP save checks the same
owner and numeric row, unchanged coordinates, untouched address fields and current
provider, credential, verification, consent and selection generations. Owner edits
or another retained result take precedence; responses and publication use saved
state. No database transaction spans provider HTTP. A concurrent replay can observe
the committed winner before its optional address is saved.

Provider failure, request cancellation, process death or an enrichment-save failure
after the initial commit can leave a valid Location without an optional address.
Normal provider unavailability retains a successful create; request cancellation
propagates, and other errors can still return 500 after persistence. A same-key
retry acknowledges that row without contacting again. Admissions already consumed
remain charged even when no address is saved. Creation adds no automatic enrichment
workflow or durable attempt: eligible wholly missing addresses can be processed
through the existing explicit [missing-address enrichment controls](../user/location-providers.md#address-enrichment-controls),
or a history import's user-wide enrichment opt-in. An already enabled workflow may
also select eligible rows during reconciliation. With no such opt-in, the
post-commit/pre-enrichment gap has no automatic recovery or globally exactly-once
external-contact guarantee.

Before any automatic threshold or duplicate-timestamp skip, a supplied valid key
gets a fresh database check for that authenticated account. If the keyed winner
is already persisted, log-location returns `success: true`, `skipped: false` and
its numeric `locationId`, preserving the first writer's data. Otherwise the skip
remains `success: true`, `skipped: true`, `locationId: null`. This check proves only
what is persisted at lookup time; a later commit is acknowledged on a keyed retry.
A lookup failure is an error, never evidence of an unpersisted skip.

Automatic keyed captures selected for persistence enter visit detection only
after their Location insert succeeds. Failed inserts, losing keyed conflicts and
persisted replays add no visit hit. Genuine time/distance/duplicate-timestamp
filtered pings still feed visit detection; a skip that instead finds a persisted
same-account keyed winner adds no hit. Accuracy rejects stay outside visit detection.
Unpersisted filtered pings have no durable creation receipt, so repeated deliveries
and hard crashes carry no universal once-only visit guarantee. Keyless automatic
pings and manual check-in retain their existing visit behavior.

Use a new key for a new logical location write and retain the same key when retrying that write. An invalid GUID is a 400.

Manual check-in intentionally bypasses automatic accuracy/time/distance filtering;
both paths retain authentication, coordinate validation, bounded admission,
persistence and immediate post-write work. Admission can return 429/503: honor
the actual `Retry-After` header. Invalid credentials need correction, and malformed
JSON/coordinates need a corrected request. Basic GPSLogger setup does not require
an idempotency key or claim its queue implements Wayfarer's retry strategy.

## Owned location history and corrections

Bearer history helpers include `GET /api/location/chronological` and
`chronological-stats` with `dateType` (`day`, `month`, `year`), `year`, and the
corresponding `month`/`day`; `has-data-for-date` and `check-navigation-availability`
provide date/navigation information. Ownership derives from the resolved account.
Cookie-only search, bulk-delete and Web location-management endpoints are separate.

Owner chronology returns all period rows for a day. Dense month/year queries may
sample map markers, while owner `totalItems` still counts the full period. This is
a map selection, with no complete-history cursor or snapshot guarantee. Use the
[owner table](../user/timeline.md#search-and-filter-locations) and
[export tools](../user/import-export.md#export-location-history) for finding and
exporting stored history.

Group history has a different selection/count contract. Cookie Web
`POST /api/groups/{groupId}/locations/query` returns its unpaged selection as
`totalItems/results`; it ignores `pageSize`/`continuationToken` and returns no
pagination flags. Bearer `POST /api/mobile/groups/{groupId}/locations/query` pages
the selected markers. Date-query `totalItems` counts bbox-filtered selected
markers before paging; the viewport fallback retains its underlying bbox count.

Both history adapters reuse the unpaged selection in `GroupTimelineService`.
Web retains raw exact subject IDs and subject blocks in the existing reader's
order. Mobile retains blank removal/ID trimming and global server timestamp/ID
ordering before paging.
For either adapter, multiple authorized subjects restrict history to a day.

For example, the [Phase C PostgreSQL same-cell month witness](https://github.com/stef-k/Wayfarer/issues/825#issuecomment-6089538971)
stored **3,001 rows**: owner chronology returned **five markers** with owner total
**3,001**; Group history returned **five markers**, `totalItems=5`, `hasMore=false`
and `isTruncated=false`. Those flags exhaust the selected markers, not stored
history. Bearer `hasMore` describes remaining selected markers, and
`nextPageToken` is null on the final page. `isTruncated` is true when `hasMore` is
true or the whole selection exceeds the current page; it can remain true on the
final page, as in a six-marker selection returned across two three-marker pages.

Bearer continuation searches a freshly recomputed selected-marker set on every
request. Malformed tokens or vanished anchors restart from the beginning. Keep
the same Group, users, date, viewport and zoom scope, and defensively deduplicate
across changing selections: this provides neither snapshot stability,
exactly-once iteration nor complete stored-history pagination.

`chronological-stats` and `check-navigation-availability` accept case-insensitive
date types without trimming, years 1–9999, applicable months 1–12, and valid
calendar days (including leap days). Invalid supplied components return 400 after
bearer resolution; missing/inactive bearer authority remains 401. MVC binding
errors return the framework's 400 response before action-level authentication.
Statistics errors use `{ "success": false, "message": "..." }`; navigation errors
use `{ "success": false }`. Unexpected statistics failures remain 500.

Statistics requires month/day for day mode and month for month mode. Navigation
retains optional month/day inputs and its existing fallback flags; a supplied day
without a month must be 1–31. Both actions ignore numeric components irrelevant to
the selected mode. Statistics filters capture/event `LocalTimestamp` over an
inclusive UTC period, including the final day/month of year 9999. Navigation uses
the server-local current date, retains all previous-navigation flags, and disables
future or unrepresentable next periods.

Maximum-date support here is limited to bearer statistics/navigation. Chronology's
shared date service still returns 400 for December 9999 and 9999-12-31 because its
successor calculation overflows. Cookie Web Timeline calendar validation and
boundary arithmetic remain separate, unchanged limitations.

`PUT /api/location/{id}` accepts a partial `LocationUpdateRequestDto`: paired
`latitude`/`longitude`, `notes`, `localTimestamp`, `activityTypeId` or `activityName`,
and explicit `clearNotes`/`clearActivity` flags. An absent field is unchanged;
clear flags take precedence. Coordinates are bounded decimal degrees and
timestamps follow the current UTC/coordinate-zone conversion. The ID is the
server's integer location ID, matched within the selected owner's rows. If a
cookie selected the account, antiforgery is required even with a bearer header.

Activity writes resolve a current positive `activityTypeId` first, then a permanent
redirect for IDs removed during catalog duplicate cleanup. The stored FK is the
survivor even after a client catalog refresh; queued Mobile IDs do not expire.
PUT's `activityName` fallback uses the database's NFC/full Unicode-folded identity
with surrounding whitespace trim only and never creates entries. Clear flags,
absent/null values, unknown and negative offline IDs retain the existing action
semantics, as do first-writer replay, ownership and response envelopes. Catalog
lookup overload still returns the existing 429/503 response with `Retry-After`.

`DELETE /api/location/{id}` uses bearer ownership and returns an ordinary success
object with `id`; a missing/non-owned row is 404. For example, a bearer-only
correction can send `PUT /api/location/123` with `{ "notes": "Corrected note" }`.
It does not grant access to another account's history or imply a bulk-delete API.

## Owned Trip Region and Place integrations

The general Trip API supports selected **bearer-owned** plan operations used by
Mobile and custom integrations. `GET /api/trips` lists the caller's Trips;
canonical public GET routes are discovery `/api/trips/public`, images
`/api/trips/public/{id}/images`, detail `/api/trips/{id}` and boundary
`/api/trips/{id}/boundary`. Public Trips permit anonymous reads. Discovery and
images expose only public Trips, even to an owner; private images return 404.
Private detail/boundary reads require a valid API token belonging to the Trip
owner; cookie ownership alone is insufficient for those actions.
Holding a token does not supply User cookie authority for the Vue editor.

Trip detail exposes the stored `updatedAt` as a **best-effort freshness hint**:
a changed value can justify refetching, but equality does not prove every nested
field is unchanged. Wall-clock stamping is not a universal, guaranteed monotonic
aggregate revision. No `version`, ETag, optimistic concurrency condition or
synchronization cursor is promised.

| Operation | Route | JSON input |
| --- | --- | --- |
| Create Region | `POST /api/trips/{tripId}/regions` | Required non-empty `name` (MVC validation); optional `notes`, `coverImageUrl`, paired `centerLatitude`/`centerLongitude`, `displayOrder`. |
| Update Region | `PUT /api/trips/regions/{regionId}` | Optional fields above; Trip association cannot change. |
| Delete Region | `DELETE /api/trips/regions/{regionId}` | No body; dependency confirmation may be required. |
| Create Place | `POST /api/trips/{tripId}/places` | Required `name`; optional `regionId`, paired `latitude`/`longitude`, `notes`, `displayOrder`, `iconName`, `markerColor`. |
| Update Place | `PUT /api/trips/places/{placeId}` | Optional fields above; `clearIcon`/`clearMarkerColor` reset defaults. |
| Delete Place | `DELETE /api/trips/places/{placeId}` | No body; dependency confirmation may be required. |

Region POST's automatic MVC model validation rejects an empty `name` with HTTP
400 and a `Name` validation error **before action-level token/ownership checks**.
With valid action inputs, missing/invalid bearer authority remains 401 and a
missing/non-owned Trip remains 404. Optional Region PUT names have their separate
partial-update contract.

Trip/Region/Place IDs are GUIDs. The authenticated account must own the Trip and
related Region. A Place can move only to an owned Region in the **same Trip**.
Without `regionId`, creation uses the Trip's special **Unassigned Places** Region.
That Region name is reserved and the special Region cannot be deleted. Supplied
coordinates must be paired and finite, with inclusive WGS84 bounds of **[-90, 90]**
for latitude and **[-180, 180]** for longitude; extrema and paired zeroes are valid.
Input uses decimal degrees, while returned Place `location`/Region `center` arrays
use `[longitude, latitude]`. A null or omitted pair creates no point on POST and
preserves the stored point on PUT; exactly one non-null component returns 400.

Successful Place POST/PUT responses use the canonical Place projection: finite
Points return numeric `[longitude, latitude]`, and absent Points return explicit
`location: null`. A successful metadata-only PUT of a historically stored
non-finite Point also returns `location: null`, meaning no usable position is
returned in that response. The stored geometry remains unchanged; this API
projection change does not delete, correct or repair persisted data.

All four mutations reject NaN, positive/negative infinity and finite out-of-range
pairs with the existing 400 string: `Latitude or Longitude is out of range.` for
Places, or `Center latitude or longitude is out of range.` for Regions. Incomplete
pairs retain `Both latitude and longitude must be provided together.` or
`Both centerLatitude and centerLongitude must be provided together.` Pair errors
take precedence over numeric errors. JSON binding still permits quoted named
floating-point literals such as `"NaN"`, which these actions reject; bare NaN is
invalid JSON and receives the existing automatic MVC validation response.

Coordinate rejection applies no other supplied fields, saves no changes and
schedules no cache warm-up. Invalid fallback Place creation does not create an
Unassigned Places Region. Explicit destination/ownership checks and reserved
Region identity errors retain precedence over coordinates.

A valid Place POST commits the Place, any newly needed fallback Region and the
Trip's `updatedAt` timestamp together through one relational save. A failed Place
insert rolls back those changes; existing Regions and Places remain intact.
Sequential requests reuse the existing fallback's ID, name and stored display
order. A new fallback has a generated GUID, the token owner's identity and
display order **0**. A supplied Place `displayOrder` wins; otherwise creation
appends at `(MAX(DisplayOrder) ?? 0) + 1`, starting at **1** in an empty Region.

Request cancellation is passed through creation reads and the final save.
Cancellation before commit leaves no partial Region/Place pair; cancellation
after commit cannot undo it. Losing the connection or response at commit can
leave the outcome unknown, so an error does not guarantee that nothing committed
and this endpoint supplies no automatic replay or idempotency guarantee.
PostgreSQL enforces at most one exact-name `Unassigned Places` Region within each
Trip, independently of owner/order. A missing-fallback request that receives a
verified `23505` naming `UX_Regions_Trip_UnassignedPlaces` may recover once after
EF rolls back its implicit transaction. Recovery uses a fresh DI-owned context,
revalidates bearer and Trip ownership, resolves exactly one correctly owned
winner, then inserts only the Place. There are at most two save attempts. Explicit
Place order is preserved; implicit append order is recomputed at the winner.
Both legitimate competing requests retain the existing 200/Place DTO response.
Place orders are not guaranteed unique or gap-free.

Malformed/ambiguous destinations, or an absent winner after collision, return 409:
`{ "error": "unassigned_region_conflict", "message": "Unassigned Places requires reconciliation." }`.
No retry occurs for explicit-Region requests, caller-owned transactions, unrelated
constraints, cancellation, timeout or unknown commit outcomes. Historical repair
requires separate maintainer approval; see [database rollout](database.md#canonical-unassigned-places-region-rollout-842).

Web Trip creation commits the Trip and one server-owned order-0 fallback in one
graph save. Form child graphs are excluded. Failed/cancelled graphs are cleared
before the normal error audit can save again. Image warm-up runs after commit;
a scheduling failure retains the created Trip and redirect with a warning.
Native/generic imports reject duplicate exact-name fallbacks with the existing
422 `validation_failed` envelope before replacement or graph mutation. Cloning
rejects such malformed source aggregates before creating a destination. Native
v2/v3 and clone compatibility still permit missing fallbacks; existing canonical
orders, metadata and ordinary Region names retain their meaning.

Legacy Region PUT recognizes an existing **Unassigned Places** Region by its
stored name, case-insensitively, regardless of display order. Its stored name and
order are protected: a supplied name must exactly match the stored spelling after
trimming, and a supplied `displayOrder` must equal the stored value. Identical
protected values are no-ops. Any actual name/order change rejects the whole
request with 400: `Cannot change the Unassigned Places region name or display order.`
This includes case-only/blank names and mixed metadata payloads. Ordinary Regions
still cannot adopt the reserved name (`Region name is reserved.`).

The reserved Region permits normalized `notes`, `coverImageUrl` (including an
empty string), and paired valid center coordinates, including zeroes. Invalid
center input applies no fields. Empty/null-only payloads and identical protected
fields alone return 200 with `success`, `message: "No changes applied."` and the
Region DTO, without saving or scheduling cache warm-up. Reserved deletion always
returns 400 (`Cannot delete the Unassigned Places region.`), even with a dependency
confirmation header. The cookie Trip Editor retains its separate restriction on
all reserved Region edits.

These PUT DTOs use null/omitted values as unchanged rather than generic patch
clear operations. Use a non-null empty notes string to clear notes; Place icon
and marker-color clear flags restore `marker` and `bg-blue`. Successful mutations
return 200 with `success` and the corresponding `place`/`region` DTO, or a bounded
delete success object. Invalid input/destination/reserved names return 400;
missing resources return 404; missing bearer authority and some non-owner checks
return 401. Do not assume one universal error envelope.

Destructive Place/Region operations can return **409** with affected dependencies,
an opaque `confirmationToken` and expiry. Review the warning, then explicitly
retry the same delete with `X-Wayfarer-Dependency-Confirmation: <confirmationToken>`
if that destruction is intended. The token is bound to owner, operation, target,
Trip and exact dependency state; changed/expired state requires a fresh warning.
Place update can also return a lifecycle concurrency 409. Never treat a 409 as
an unconditional no-body delete success.

For example, create a Region with `{ "name": "Athens" }`, then create a Place
under that owned Trip with `{ "name": "Museum", "regionId": "<returned-guid>",
"latitude": 37.98, "longitude": 23.73 }`. Partial notes updates use
`{ "notes": "Visit in the morning" }` at the corresponding PUT route.

Other existing bearer capabilities include limited Trip metadata, Segment notes
and Area metadata edits, public discovery and public-Trip cloning. Mobile Group,
recent-visit, hosted-routing and SSE families retain membership, ownership,
visibility and provider gates. These are limited API contracts, not unrestricted
account access or full Web editor parity; Web invitations, provider backfill,
account/admin tools and editor mutations keep their cookie/role/antiforgery rules.

## Saved Trip measurements and native KML

A Segment has one active saved distance/duration set. `Automatic`/`Manual` owns
duration intent independently of route provenance. Protected provider proposal
acceptance establishes provider estimates; ordinary editor JSON cannot assert
provider authority. Qualified persistent provenance comes from the saved
`RouteProvider` and persistent storage marker, with a supported mapping mode when
present. Provider credentials,
verification, availability and current planning speeds do not participate in
reading, transferring or cloning an already accepted result.

New native Trip exports declare `WayfarerSchemaVersion=3`. Geometry, identities,
waypoint integrity, ownership and numerical validity remain checked. The native
version controls the following distinct representations:

| Version | Duration | Measurement handling |
| --- | --- | --- |
| v1 / recognized versionless legacy | `DurationMin` | Established Manual normalization and geometry/planning reconciliation. |
| v2 | Integer `DurationSeconds` | Established canonical compatibility checks; no reinterpretation as fractional time. |
| v3 | Integer `DurationTicks` | Exact active `TimeSpan` ticks; values must be nonnegative multiples of 10, matching PostgreSQL interval microseconds. |

V3 writes `DistanceKm` with round-trip double precision and carries existing
credential-free source metadata in `RouteProvenance` JSON: `Provider`,
`StorageMode`, `MappingMode`, `GeneratedAt`, `Attribution`, `InstructionsJson`,
`TransportProfileId`, `ProviderConfigurationId`, `ProviderConfigurationVersion`.
Configuration/profile IDs in this provenance are historical scalar identities,
not execution authority. The actual planning profile still resolves through
`TransportProfileKey`. JSON members are bounded and duplicate/unknown claims,
unsupported retention rights and invalid normalized instructions are rejected.
Neither `DurationTicks` in older versions nor `DurationSeconds` in v3 is accepted.

V3 imports and clones preserve valid nulls, active values and Manual intent
without numerical recalculation. Finite nonnegative historical estimates remain
valid even when current geometry/planning produces different numbers. Missing
source metadata stays unknown; numerical differences never invent provider
attribution. Export does not rewrite existing rows. Manual geometry/anchor changes
clear obsolete provider provenance and calculate the deliberate Wayfarer result;
unchanged-route metadata edits preserve the saved set. Planning-speed mutations
retain qualified provider estimates and reconcile Wayfarer estimates.

## SSE families

`SseService` sends ordinary SSE `data:` frames containing JSON and optional comment heartbeats. Transport is best-effort and bounded; durable database state is authoritative.

Current web streams include:

- `GET /api/sse/stream/location-update/{username}`: anonymous only when that user's Timeline is effectively public and live. Eligibility is checked before subscription and again during protected delivery.
- `GET /api/sse/import`: cookie-authenticated caller's import/enrichment reload-hint channel. It derives the protected channel only from the authenticated `NameIdentifier`; callers cannot select another user, import or workflow channel. This content-free SSE emits only `import-state` and `enrichment-state` hints, and clients reload authoritative relational state rather than treating the event as display data.
- `GET /api/sse/group-notifications`: cookie-authenticated caller's invitation/membership reload-hint channel.
- `GET /api/sse/group/{groupId}`: cookie Identity/`NameIdentifier` event stream for active members of an unarchived Group, with heartbeat and protected delivery-time recipient membership and location/deletion source-audience checks. Group roles do not bypass location privacy.

Mobile equivalents require request-owned bearer authority for an active token
owner; cookies alone do not authorize them. `GET /api/mobile/sse/visits` subscribes
only to that owner's channel. `GET /api/mobile/sse/group/{groupId}` retains the
same active membership and protected delivery-time recipient/source privacy
checks as the Web Group stream. Heartbeats bypass protected-frame leases;
immediate credential revocation of an already-open stream is not promised.

Content-free reload hints are deliberately small. The exact current hint types are `import-state`, `enrichment-state`, `invitation-state` and `membership-state`. On a hint, fetch current authenticated state; do not infer durable details from the hint itself.

Location/group streams carry typed JSON events such as location updates/deletions and membership-related events. Consumers must tolerate reconnect/reload rather than treating SSE as a durable queue.

## Client rules worth preserving

- Propagate cancellation for abandoned HTTP work.
- Honor ownership/role failures rather than retrying them as transient errors.
- Honor `Retry-After` on bounded admission failures.
- Reuse a GUID `Idempotency-Key` only for retries of the same location-ingestion operation.
- Treat SSE as a presentation signal over durable HTTP/database state.
- Do not log bearer tokens, provider credentials or private request payloads while diagnosing failures.
