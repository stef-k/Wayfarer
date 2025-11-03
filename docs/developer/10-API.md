# API

Auth Model
- Bearer tokens via per-user `ApiToken` entries. Include header: `Authorization: Bearer <token>`.
- Public trips require no auth; private trips require ownership.

Base Controller
- `Areas/Api/Controllers/BaseApiController` extracts token and resolves `ApplicationUser` and role. Includes helpers to sanitize floats/points.

Selected Endpoints
- General
  - `GET /api/settings` — returns thresholds used by mobile guidance.
  - `GET /api/activity` — list of activity types.
- Trips
  - `GET /api/trips` — list trips for the authenticated user.
  - `GET /api/trips/{id}` — full trip structure; public or owner-only.
  - `GET /api/trips/{id}/boundary` — bounding box for tile prefetch.
- Locations
  - `POST /api/location/log-location` — background logging (threshold-aware on server).
  - `POST /api/location/check-in` — manual check-in with rate limits.
- Mobile (Groups & SSE)
  - `GET /api/mobile/groups/{groupId}/members` — members.
  - `POST /api/mobile/groups/{groupId}/locations/latest` — latest per member.
  - `POST /api/mobile/groups/{groupId}/locations/query` — spatial/time filtered query with pagination.
  - `GET /api/mobile/sse/location-update/{userName}` and `.../group-location-update/{groupId}` — SSE channels for live updates.

Conventions
- JSON camelCase; timestamps in UTC.
- Sanitized geometry SRID 4326; numeric NaN/Infinity dropped.

Error Handling
- 401 when token missing/invalid for protected resources.
- 403 reserved for role restrictions; 404 for not found.
- 400 for invalid payloads.

Notes
- Avoid hardcoding server domains in client apps; treat server URL as a user setting.

