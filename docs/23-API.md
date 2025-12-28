# API

The Wayfarer API provides RESTful endpoints for mobile app integration and external access.

---

## Authentication

- **Bearer tokens** via per-user `ApiToken` entries.
- Include header: `Authorization: Bearer <token>`
- Public endpoints (public trips, public timeline) require no auth.
- Private resources require token ownership.

---

## Base Controller

- `Areas/Api/Controllers/BaseApiController` provides common functionality:
  - Token extraction and user resolution
  - Role-based access control
  - Helpers to sanitize floats and coordinates

---

## Endpoints Reference

### General

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/settings` | Application settings (thresholds, limits) |
| GET | `/api/activity` | List of activity types |

### Trips

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/trips` | List trips for authenticated user |
| GET | `/api/trips/{id}` | Full trip structure (public or owner) |
| GET | `/api/trips/{id}/boundary` | Bounding box for tile prefetch |

### Trip Tags

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/trips/{id}/tags` | Get trip tags |
| POST | `/api/trips/{id}/tags` | Add tag to trip |
| DELETE | `/api/trips/{id}/tags/{tag}` | Remove tag from trip |

### Locations

| Method | Endpoint | Description |
|--------|----------|-------------|
| POST | `/api/location/log-location` | Background GPS logging (threshold-aware) |
| POST | `/api/location/check-in` | Manual check-in (rate-limited) |
| GET | `/api/location/stats` | User location statistics |

### Visits

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/visits` | User visit history |
| GET | `/api/visits/{id}` | Single visit details |
| PUT | `/api/visits/{id}` | Update visit |
| DELETE | `/api/visits/{id}` | Delete visit |

### Groups

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/groups` | User's groups |
| GET | `/api/groups/{id}` | Group details |
| GET | `/api/groups/{id}/members` | Group members |

### Invitations

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/invitations` | Pending invitations for user |
| POST | `/api/invitations/{id}/accept` | Accept invitation |
| POST | `/api/invitations/{id}/decline` | Decline invitation |

### Mobile-Specific

| Method | Endpoint | Description |
|--------|----------|-------------|
| GET | `/api/mobile/groups/{groupId}/members` | Group members |
| POST | `/api/mobile/groups/{groupId}/locations/latest` | Latest location per member |
| POST | `/api/mobile/groups/{groupId}/locations/query` | Spatial/time filtered query |

---

## SSE (Server-Sent Events)

Real-time streaming endpoints for live updates:

| Endpoint | Description |
|----------|-------------|
| `/api/sse/stream/location-update/{userName}` | User location updates |
| `/api/sse/stream/group-location-update/{groupId}` | Group location updates |
| `/api/sse/stream/visits` | Visit start/end notifications |
| `/api/sse/stream/invitations` | Invitation notifications |
| `/api/sse/stream/memberships` | Group membership changes |
| `/api/sse/stream/job-status` | Background job status updates |
| `/api/sse/stream/import-progress` | Import progress updates |

### SSE Event Types

```json
// Location update
{ "type": "location", "userId": "...", "latitude": 37.97, "longitude": 23.72 }

// Visit started
{ "type": "visit_started", "visitId": "...", "placeName": "...", "arrivedAtUtc": "..." }

// Job status
{ "type": "job_status", "jobName": "...", "status": "Completed" }
```

---

## Conventions

- JSON with **camelCase** property names
- Timestamps in **UTC** (ISO 8601)
- Geometry uses **SRID 4326** (WGS84)
- Numeric `NaN`/`Infinity` values dropped from responses
- Nullable fields omitted when null

---

## Error Handling

| Status | Meaning |
|--------|---------|
| 400 | Invalid request payload |
| 401 | Missing or invalid token |
| 403 | Insufficient permissions |
| 404 | Resource not found |
| 429 | Rate limit exceeded |

---

## Rate Limiting

- Check-in endpoint: limited to prevent spam
- Threshold enforcement based on application settings
- Rate limit headers included in responses

---

## Client Best Practices

- Avoid hardcoding server domains; treat server URL as user setting.
- Handle SSE reconnection gracefully.
- Respect rate limits and thresholds.
- Cache trip data when appropriate.

