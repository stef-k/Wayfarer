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

`api/mobile/*` controllers use the mobile token accessor and are bearer/API-token paths. They do not create an ASP.NET authentication principal merely from the token.

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

## Antiforgery boundary

Browser mutations that rely on cookie Identity use MVC antiforgery, normally through `[ValidateAntiForgeryToken]`. This includes the Trip Editor mutation family and many group/invitation/backfill/browser mutations.

`PUT /api/location/{id}` is the explicit dual-auth boundary: `CookieFirstAntiforgeryAttribute` requires antiforgery whenever an authenticated cookie selected the request, even if a bearer header is also present. Only a successfully resolved bearer request with no selected cookie authority is token-free.

Bearer-only mobile/integration mutations do not require MVC antiforgery. Do not remove antiforgery from a browser mutation merely because a bearer path exists elsewhere.

## Route families

| Family | Current responsibility | Typical authority |
| --- | --- | --- |
| `/api/version` | Application version | anonymous |
| `/api/icons`, `/api/tags`, `/api/activity`, `/api/settings` | UI/reference data | endpoint/controller rules |
| `/api/users` | user lookup, public stats, authenticated activity/management actions | mixed public, cookie roles and ownership |
| `/api/trips` | trip reads/search, public trip discovery/images and legacy trip mutations/clone | public where explicitly exposed; otherwise token/cookie ownership |
| `/api/trips/{tripId}/editor` | Vue Trip Editor state, metadata, regions, areas, places, segments, tags, share progress and geocode search | User cookie + ownership; mutations antiforgery |
| `/api/trip-editor/{tripId}/segments/{segmentId}/route-proposals` | external route proposal generation | User cookie + ownership + antiforgery |
| `/api/location` | location ingestion, queries, edits, deletion and stats | token/cookie depending on action; ownership required |
| `/api/visit`, `/api/backfill` | visit queries/mutations and visit backfill | authenticated/owned browser API |
| `/api/groups`, `/api/invitations` | group membership, visibility, location sharing and invitations | authenticated membership/role; browser mutations antiforgery |
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
| `timestamp` | Capture DateTime; supply ISO UTC with `Z`. No-offset local time is converted using coordinates. Non-nullable DTO defaults are not required-field validation. |
| `accuracy`, `altitude`, `speed` | Optional metres, metres, metres/second respectively. |
| `locationType`, `notes`, `activityTypeId` | Optional capture description, notes and existing activity ID. |
| `source`, `isUserInvoked`, `provider`, `bearing` | Optional origin, invocation flag, sensor/provider and bearing in degrees. |
| `appVersion`, `appBuild`, `deviceModel`, `osVersion`, `batteryLevel`, `isCharging` | Optional capture metadata; battery percentage is 0–100. |

Omit unavailable measurements. Metadata does not change endpoint filtering:
`isUserInvoked` on log-location is not a check-in bypass. Automatic capture applies
accuracy, time and distance thresholds and duplicate-timestamp rules. A 200 can
be `{ "success": true, "skipped": true, "locationId": null }`, with no new row.
A saved automatic point returns `skipped: false` and its integer `locationId`.
Check-in success returns `message` and the publication-safe `location` object.
Check actual saved history rather than equating HTTP success with persistence.

`POST /api/location/check-in` and `POST /api/location/log-location` accept an optional `Idempotency-Key` header containing a GUID. The key is unique per user. A persisted replay returns the prior success before new-work admission; concurrent duplicate persistence is reconciled against the same database invariant.

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

`PUT /api/location/{id}` accepts a partial `LocationUpdateRequestDto`: paired
`latitude`/`longitude`, `notes`, `localTimestamp`, `activityTypeId` or `activityName`,
and explicit `clearNotes`/`clearActivity` flags. An absent field is unchanged;
clear flags take precedence. Coordinates are bounded decimal degrees and
timestamps follow the current UTC/coordinate-zone conversion. The ID is the
server's integer location ID, matched within the selected owner's rows. If a
cookie selected the account, antiforgery is required even with a bearer header.

`DELETE /api/location/{id}` uses bearer ownership and returns an ordinary success
object with `id`; a missing/non-owned row is 404. For example, a bearer-only
correction can send `PUT /api/location/123` with `{ "notes": "Corrected note" }`.
It does not grant access to another account's history or imply a bulk-delete API.

## Owned Trip Region and Place integrations

The general Trip API supports selected **bearer-owned** plan operations used by
Mobile and custom integrations. `GET /api/trips` lists the caller's Trips;
`GET /api/trips/{id}` permits private owners and explicitly public Trip reads.
Holding a token does not supply User cookie authority for the Vue editor.

| Operation | Route | JSON input |
| --- | --- | --- |
| Create Region | `POST /api/trips/{tripId}/regions` | Required `name`; optional `notes`, `coverImageUrl`, paired `centerLatitude`/`centerLongitude`, `displayOrder`. |
| Update Region | `PUT /api/trips/regions/{regionId}` | Optional fields above; Trip association cannot change. |
| Delete Region | `DELETE /api/trips/regions/{regionId}` | No body; dependency confirmation may be required. |
| Create Place | `POST /api/trips/{tripId}/places` | Required `name`; optional `regionId`, paired `latitude`/`longitude`, `notes`, `displayOrder`, `iconName`, `markerColor`. |
| Update Place | `PUT /api/trips/places/{placeId}` | Optional fields above; `clearIcon`/`clearMarkerColor` reset defaults. |
| Delete Place | `DELETE /api/trips/places/{placeId}` | No body; dependency confirmation may be required. |

Trip/Region/Place IDs are GUIDs. The authenticated account must own the Trip and
related Region. A Place can move only to an owned Region in the **same Trip**.
Without `regionId`, creation uses the Trip's special **Unassigned Places** Region.
That Region name is reserved and the special Region cannot be deleted. Supplied
coordinates must be paired and finite, with inclusive WGS84 bounds of **[-90, 90]**
for latitude and **[-180, 180]** for longitude; extrema and paired zeroes are valid.
Input uses decimal degrees, while returned Place `location`/Region `center` arrays
use `[longitude, latitude]`. A null or omitted pair creates no point on POST and
preserves the stored point on PUT; exactly one non-null component returns 400.

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
Region identity errors retain precedence over coordinates. Fallback Region and
Place creation still use separate saves; this validation rule does not promise
rollback if a later valid Place save fails.

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

## SSE families

`SseService` sends ordinary SSE `data:` frames containing JSON and optional comment heartbeats. Transport is best-effort and bounded; durable database state is authoritative.

Current web streams include:

- `GET /api/sse/stream/location-update/{username}`: anonymous only when that user's Timeline is effectively public and live. Eligibility is checked before subscription and again during protected delivery.
- `GET /api/sse/import`: cookie-authenticated caller's import/enrichment reload-hint channel. It derives the protected channel only from the authenticated `NameIdentifier`; callers cannot select another user, import or workflow channel. This content-free SSE emits only `import-state` and `enrichment-state` hints, and clients reload authoritative relational state rather than treating the event as display data.
- `GET /api/sse/group-notifications`: cookie-authenticated caller's invitation/membership reload-hint channel.
- `GET /api/sse/group/{groupId}`: cookie-authenticated group-member event stream with heartbeat and delivery-time membership checks.

Mobile equivalents use bearer authority, notably `GET /api/mobile/sse/visits` and `GET /api/mobile/sse/group/{groupId}`.

Content-free reload hints are deliberately small. The exact current hint types are `import-state`, `enrichment-state`, `invitation-state` and `membership-state`. On a hint, fetch current authenticated state; do not infer durable details from the hint itself.

Location/group streams carry typed JSON events such as location updates/deletions and membership-related events. Consumers must tolerate reconnect/reload rather than treating SSE as a durable queue.

## Client rules worth preserving

- Propagate cancellation for abandoned HTTP work.
- Honor ownership/role failures rather than retrying them as transient errors.
- Honor `Retry-After` on bounded admission failures.
- Reuse a GUID `Idempotency-Key` only for retries of the same location-ingestion operation.
- Treat SSE as a presentation signal over durable HTTP/database state.
- Do not log bearer tokens, provider credentials or private request payloads while diagnosing failures.
