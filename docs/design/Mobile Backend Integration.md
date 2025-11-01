Mobile Backend Integration & Operations
======================================

### API Surface (Mobile Areas)

Mobile clients interact with a token-secured API surface under `api/mobile`:

| Endpoint | Method | Description |
| --- | --- | --- |
| `/api/mobile/groups?scope=all\|managed\|joined` | `GET` | Lists groups for the authenticated caller. Requires `Authorization: Bearer <API token>`. |
| `/api/mobile/groups/{groupId}/members` | `GET` | Returns `GroupMemberDto[]` including colour hints and per-user SSE channel names. |
| `/api/mobile/groups/{groupId}/locations/latest` | `POST` | Provides latest locations for allowed members. Optional `includeUserIds` body narrows the result set. |
| `/api/mobile/groups/{groupId}/locations/query` | `POST` | Paginates historic locations within the supplied bounds. Supports `pageSize` (defaults 200, max 500) and `continuationToken`. |
| `/api/mobile/sse/location-update/{userName}` | `GET` | Subscribes to per-user SSE channel via API token auth. Emits enriched payloads plus heartbeat comment frames. |
| `/api/mobile/sse/group-location-update/{groupId}` | `GET` | Subscribes to group-level SSE channel (members only) with the same payload contract and heartbeat cadence. |

### Payload Conventions

- `GroupMemberDto` and `MobileGroupSummaryDto` expose documented properties for mobile while remaining optional for web flows.
- `GroupLocationsQueryResponse` wraps query results with pagination metadata (`totalItems`, `returnedItems`, `pageSize`, `hasMore`, `nextPageToken`, `isTruncated`).
- SSE events are serialized via `MobileLocationSseEventDto`, ensuring both legacy PascalCase fields (`LocationId`, `TimeStamp`) and camel-case mobile fields (`locationId`, `timestampUtc`, `userId`, `userName`, `isLive`) are present in every frame.

### Configuration & Operational Controls

`appsettings*.json` surface mobile-specific knobs:

```jsonc
"MobileGroups": {
  "Query": {
    "DefaultPageSize": 200,
    "MaxPageSize": 500
  }
},
"MobileSse": {
  "HeartbeatIntervalMilliseconds": 20000
}
```

- Adjust pagination caps to balance payload size vs. latency.
- Tune heartbeat intervals per environment (development defaults to 5 s; production 20 s).
- Options bind via `MobileSseOptions` and flow into `MobileSseController`, keeping heartbeat cadence configurable without code changes.

### Monitoring & Troubleshooting

- **SSE**: `SseService` logs write failures and removes stale clients. Monitor these logs (and connection counts) during rollout.
- **Throughput**: `GroupTimelineService` clamps to configured caps and honours group visibility rules. `HasMore`/`nextPageToken` signal when clients must page.
- **Authentication**: unauthorised requests return structured JSON (`401/403`) through the existing API middleware.

### Curl Examples

```bash
# List groups
curl -sS -H "Authorization: Bearer $TOKEN" \
  "https://<host>/api/mobile/groups?scope=all"

# List members
curl -sS -H "Authorization: Bearer $TOKEN" \
  "https://<host>/api/mobile/groups/$GROUP_ID/members"

# Latest locations
curl -sS -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"includeUserIds":["friend","me"]}' \
  "https://<host>/api/mobile/groups/$GROUP_ID/locations/latest"

# Paginated query
curl -sS -H "Authorization: Bearer $TOKEN" \
  -H "Content-Type: application/json" \
  -d '{"minLng":-180,"minLat":-90,"maxLng":180,"maxLat":90,"zoomLevel":10,"pageSize":200}' \
  "https://<host>/api/mobile/groups/$GROUP_ID/locations/query"

# SSE subscription
curl -N -H "Authorization: Bearer $TOKEN" \
  "https://<host>/api/mobile/sse/location-update/me"
```

### Rollout Checklist

1. Issue API tokens for participating mobile users (`Authorization: Bearer <token>`).
2. Configure `MobileGroups:Query` caps and `MobileSse` heartbeat intervals per environment.
3. Smoke-test list/members/latest/query endpoints and SSE channels with the curl commands above.
4. Monitor logs for `SseService` warnings/errors and `GroupTimelineService` failures during pilot rollout.
5. Coordinate the mobile release to ensure clients respect pagination (`hasMore`/`nextPageToken`) and SSE payload contracts before broad availability.
