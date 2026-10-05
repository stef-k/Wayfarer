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

`POST /api/location/check-in` and `POST /api/location/log-location` accept an optional `Idempotency-Key` header containing a GUID. The key is unique per user. A persisted replay returns the prior success before new-work admission; concurrent duplicate persistence is reconciled against the same database invariant.

Use a new key for a new logical location write and retain the same key when retrying that write. An invalid GUID is a 400.

Manual check-in intentionally bypasses the automatic time/distance filtering used by normal location logging; both paths retain validation, bounded admission, persistence and immediate post-write work.

## SSE families

`SseService` sends ordinary SSE `data:` frames containing JSON and optional comment heartbeats. Transport is best-effort and bounded; durable database state is authoritative.

Current web streams include:

- `GET /api/sse/stream/location-update/{username}`: anonymous only when that user's Timeline is effectively public and live. Eligibility is checked before subscription and again during protected delivery.
- `GET /api/sse/import`: cookie-authenticated caller's import/enrichment reload-hint channel.
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
