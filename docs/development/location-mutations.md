---
title: Protected Location mutations
---

# Protected Location mutations

[Issue #947](https://github.com/stef-k/Wayfarer/issues/947) approves
`location-mutation-1` / `location-values-1`. This is the single-record Web/API
editing and deletion contract. Creation, keyed original replay, imports, bulk
commands and account erasure keep their distinct owners. #951 batch transport
is outside this contract. There is no feature flag or later enablement step.

## Owner detail and execution authority

Read `GET /api/location/history/{id}?viewerZone=UTC`. Detail alone accepts active
owner cookie-first authority as well as the existing bearer authority; collection
and overview reads remain bearer-only. The existing `owner-detail-1` projection
and unavailable facts remain present. One consistent snapshot also contains:

```json
{"locationMutation":{"contract":"location-mutation-1","normalization":"location-values-1","baseId":"83e8095d-45b0-4dc7-a267-89a4ee119d67","baseHandle":"H","issuedAtUtc":"2026-10-29T19:00:00Z","expiresAtUtc":"2026-10-29T19:15:00Z","originalCreateAddressPublication":"closed","baseValues":{"uuid":{"state":"known","value":null},"position":{"state":"known","value":{"latitude":37.98,"longitude":23.72}},"address":{"state":"unavailable","reason":"unrepresentableStoredValue"},"eventUtc":{"state":"known","value":"2026-10-29T08:15:00.123456Z"},"activityTypeId":{"state":"known","value":7},"notesHtml":{"state":"known","value":null},"altitude":{"state":"known","value":null},"deleteContent":{"state":"known","value":{"format":"location-delete-storage-1","fingerprint":"sha256:09a4d5e617c35b49c0dd6190bf97540a6fa43c66e2bd05bf4ccf813ded219198"}}}}}
```

`H` and the delete digest above illustrate shape, not usable execution/proof
fixtures. Never reconstruct delete proof from display values. The eight
`baseValues` slots are exact; position has only latitude/longitude, independently
of the outer record's display status.

A slot is exactly `{"state":"known","value":T}` (value is mandatory even when
null), or `{"state":"unavailable","reason":"unrepresentableStoredValue"}`.
Clients missing retained dependencies may instead send reason
`notRetainedOriginal`. Neither unavailable nor null means Unchanged. Unavailable
required originals reject with `baseUnavailable`.

The Data Protection handle authenticates contract/normalization, owner, target ID,
UUID including null, baseId, issuance and a nonrenewable 15-minute execution
expiry. It contains no editable snapshot. Renewing authority never changes
retained originals. Keep the application's existing Data Protection keys for
expired-handle recovery; receipt cleanup does not remove keys.

`originalCreateAddressPublication` is exactly `pending` or `closed`, from the same
snapshot. The internal nullable token is never serialized. Closed excludes the
original-create publisher, including after Set → Clear; it does not exclude later
independently authorized enrichment. This evidence is not an expected group or a
general row revision.

## Strict commands and selected originals

`POST /api/location/{id}/mutations/{baseId}/{actionId}` accepts exactly `contract`,
`normalization`, `adapter`, `baseHandle`, `kind`, `expected`, and nonempty `changes`
for updates only. `kind` is `update` or `delete`. `adapter` is `owner` or
`web-edit`; Web-only fields require active cookie ownership and antiforgery, even
with a bearer header. Owner supports cookie-first or bearer authority. Put
credentials and antiforgery in headers, outside immutable body bytes.

JSON names are case-sensitive. Unknown/duplicate members at every level,
contradictory tagged objects, invalid UTF-8/Unicode, wrong types and invalid new
coordinates/times reject. Expected `uuid` is always required and must match the
signed handle and final row. Other expected groups are exactly this union:

| Selected change | Additional expected groups | Exact change shape |
| --- | --- | --- |
| Notes | notesHtml | `{"op":"set","html":"..."}` or `{"op":"clear"}` |
| Activity | activityTypeId | `{"op":"set","id":7}` or `{"op":"clear"}` |
| position | position, address | `{"op":"set","latitude":0,"longitude":0}` |
| eventUtc | eventUtc | `{"op":"set","value":"2026-10-29T08:16:00.123456Z"}` |
| Web altitude | altitude | `{"op":"set","value":12.5}` or `{"op":"clear"}` |
| Web manualAddress | position, address | `{"op":"set","fields":{"place":"My place","country":null}}` |
| Delete | content | No changes member; content is the original deleteContent slot |

Omission preserves final locked fields. Null change objects, `unchanged`, Clear
with a value, and position/time Clear reject. Combined changes compare/apply
atomically. A rename does not change Activity identity; Set resolves a live
positive integer or retained redirect under the transaction, otherwise rejects
without clearing. Names are not mutation identity.

`Address` has all fourteen members: `address`, `fullAddress`, `addressNumber`,
`streetName`, `postCode`, `place`, `region`, `country`, `providerAddressLine1`,
`resolvedFeatureName`, `resolvedFeatureType`, `reverseGeocodingProvider`,
`reverseGeocodingStorageMode`, `reverseGeocodedAt`. Each is text/null except the
last, which is UTC instant/null. All-null is known. Manual `fields` is a nonempty
subset of the first eight members; trim/blank-to-null applies only to explicit
manual components. Prefilled untouched fields are not submitted as intent.

IDs are positive int32; expected Activity may be any int32/null. UUIDs use
lowercase hyphenated form. Numbers are finite round-tripped binary64, with signed
zero comparing as zero. Position uses WGS84 latitude [-90,90], longitude
[-180,180]; zero is valid. Text compares ordinally without folding or trimming,
except the approved Notes normalization. Null and empty differ.

Instants use Gregorian years 0001–9999, seconds 00–59 and a literal `Z`, with no
offset/wall-time form. New Set allows at most six fractional digits and excludes
infinity-conversion endpoints. Expected originals allow nine: retain the first
seven digits, then convert ticks relative to PostgreSQL's epoch
`630822816000000000` by integer division toward zero to microseconds. Thus
`2026-10-29T08:15:00.123456789Z` compares as `.123456Z`, while
`1999-12-31T23:59:59.999999900Z` compares as `2000-01-01T00:00:00Z`.

`location-values-1` uses server `RichNotes.Normalize` on expected/final Notes and
Set before storage. Null remains null; empty, whitespace and `<p><br></p>` become
empty. Supported rich HTML remains supported; clients do not copy this sanitizer
or replace originals with a server winner. Future normalization changes require
a new identity or explicit `baseNormalizationUnsupported`; retained same-action
receipts recover before current catalog/normalization validation.

Example Notes command (the handle is illustrative):

```json
{"contract":"location-mutation-1","normalization":"location-values-1","adapter":"owner","baseHandle":"example-handle","kind":"update","expected":{"uuid":{"state":"known","value":null},"notesHtml":{"state":"known","value":null}},"changes":{"notes":{"op":"clear"}}}
```

## Immutable action fingerprints

Before a possible handoff, persist configured server/account, target, one random
base/action identity, adapter, exact UTF-8 body bytes, handle, desired generation
and the digest. Never regenerate bytes or create an action UUID on retry.
Content-Encoding is identity; no BOM. Existing request-size limits still apply.

`frame(bytes)` is unsigned uint32 network-order byte length followed by bytes.
SHA-256 covers exactly seven frames: UTF-8 `location-mutation-request-1`, exact
subject ID, canonical positive decimal target ID, lowercase base UUID, lowercase
action UUID, adapter, and exact received body bytes. Fingerprint is `sha256:` plus
64 lowercase hex digits. No property-order, Unicode, whitespace, HTML, number,
escape or timestamp normalization precedes hashing. Headers are excluded.

Fixed vectors use owner `owner-a`, target 421, base
`83e8095d-45b0-4dc7-a267-89a4ee119d67`, action
`24cddbea-057b-4f78-8ccf-b0aa254aa011`, adapter `owner`, and the one-line example
above without its final newline. Prefix byte counts are 27,7,3,36,36,5.

| Body | Body/framed bytes | Digest after sha256: |
| --- | --- | --- |
| Example V1 | 263/405 | 961954f23232166d67d4495d4f7370b4d8be7acb47c8d90bdff401d91a3322ce |
| V2: replace notes change with `{"op":"set","html":"<p>Αθήνα</p>"}` using literal UTF-8 | 288/430 | b7c9900c92a43eeed2934fa334a39ce7eb1b7fc41f94bed3b4d4e1b13e2ebdbd |
| V3: append one LF byte to V1 | 264/406 | 3dad01e294e9c69bac41ba4afa3e6730a0a03ae1e2f47ac7ff4befc60ffea9d5 |

## Receipts, transactions and recovery

`LocationMutationOperations` has a unique owner/base/action binding and indexed
cleanup eligibility. It stores nullable key/target, adapter/kind/normalization,
request digest, acceptance/expiry/deadline, nonce and compact terminal result.
It stores no body, HTML, editable snapshot or executable retry payload. There is
no Location FK; deliberate account erasure cascades its receipts.

Every known-row envelope contains contract, normalization, adapter, subjectId,
locationId, uuid, baseId, actionId, kind, fingerprint and receiptRetainUntilUtc.
Applied/rejected add completedAtUtc and futureEffects false. Applied adds changed
and detailUrl (null for delete). Update adds addressOutcome
unchanged/replaced/cleared/manual and providerOutcome
notRequested/succeeded/unavailable. Rejected adds code and optional staleGroups,
without winner values. Pending has observedAtUtc, executeBeforeUtc,
futureEffects true and retryAfterSeconds 2, with no completed/changed result.

`GET` the operation route with `X-Location-Base: <original handle>` performs
read-only recovery. Known terminal rows win before target existence and new-work
validation. Exact duplicates never acquire an executor. Different bytes/target
reject this invocation as `operationIdentityReused`, leaving the original intact.
Clients validate every correlation member against their pre-send digest, including
the first recovered receipt after complete response loss. HTTP alone is no proof.

Account fence precedes operation/provider/Location locks in READ COMMITTED
transactions. Ordinary effects and terminal receipts commit together. Changed
coordinates first reserve Pending; only that invocation owns its nonce. Provider
HTTP holds no SQL locks, gets at most 15 seconds, and cannot extend the fixed
`min(reservation + 120 seconds, handle expiry)` executor deadline. Current
credentials, originals, nonce and database `clock_timestamp()` are checked after
final lock waits. Optional failure/denial discards output and completes valid
coordinates; a missed executor deadline can reject the operation. No takeover,
heartbeat, mutation retry worker or outbox exists. Ambiguous COMMIT remains Unknown.

Recovery takes the same account fence. Expired Pending projects rejected
`operationExecutionExpired` at its fixed deadline without writing during GET;
a later applied result cannot follow it. A writer already beyond its final
check holds the fence through commit; recovery waits for its terminal receipt.

No-row envelopes contain only contract, subjectId, route locationId/baseId/actionId,
observedAtUtc, state unknown, code, futureEffects, and expiresAtUtc when signed
proof exists. Before expiry: operationNotObserved / true. After synchronized
expiry: operationHistoryExpired / false plus historicalOutcome unknown. Missing
proof: operationProofUnavailable / null. No inferred digest/kind/UUID/result.
Storage/auth/lock failures do not prove expiry. Old base IDs cannot be paired with
new handles after cleanup.

After terminal evidence, fetch fresh detail before publishing a current baseline
or settling newer work. Failed detail reads retain the receipt and do not initiate
a replacement save. A receipt attributes only its captured generation. After old
future effects are excluded, unchanged retained originals permit a new conditional
action under fresh authority. Complete desired-state satisfaction is separately
labelled historical outcome unknown; mismatches require retained drafts and
informed conflict resolution. Address-dependent satisfaction also requires fresh
closed original-create publication evidence. Matching values alone cannot exclude
Pending publishers. See the [mobile handoff](../planning/947-mobile-handoff.md).

For a previously observed keyless target, detail GET with the expired original
handle may return 404 with exactly `contract:location-target-absence-1`, subjectId,
locationId, uuid:null, baseId, state:absent, observedAtUtc, only after current owner
auth, account fence and a fresh actual-ID absence read. Foreign/mismatched rows and
ordinary 404 have no such proof. It proves current absence, not historical actor
or permanent non-resurrection. Keyed recovery uses affirmative deleted-key evidence.

## Stored deletion proof and publication effects

`location-delete-storage-1` starts with a framed domain, then each ordered field
as `frame(fieldName) || nullTag || frame(valueBytes)`. SQL NULL uses tag 0 and an
empty value frame; present uses tag 1. SHA-256 output uses the same digest syntax.
The exact ordered PostgreSQL columns are:

1. UserId, Id, IdempotencyKey, Coordinates, LocalTimestamp, Timestamp, Notes,
   ActivityTypeId, TimeZoneId.
2. Address, FullAddress, AddressNumber, StreetName, PostCode, Place, Region,
   Country, ProviderAddressLine1, ResolvedFeatureName, ResolvedFeatureType,
   ReverseGeocodingProvider, ReverseGeocodingStorageMode, ReverseGeocodedAt.
3. Accuracy, Altitude, Speed, Bearing, LocationType, Source, Provider, AppVersion,
   AppBuild, DeviceModel, OsVersion, IsUserInvoked, IsCharging, BatteryLevel.

Text uses convert_to UTF8; integers/bools/UUID use int4send/boolsend/uuid_send;
doubles use float8send, preserving nonfinite/signed-zero encodings; timestamps use
timestamptz_send including infinity; geometry uses ST_AsEWKB(geometry,'XDR'),
without validation/repair. Same-snapshot extraction precedes ordinary decoding.
Derived history, observation evidence and the publication token are excluded.
This is logical stored-value identity, not heap/TOAST bytes or xmin. Format changes
require a new immutable identity. Raw Notes changes stale delete even if sanitized
content is equal. Content proof, keyed marker, dependent deletion, physical removal
and terminal receipt share one transaction; damaged rows need no edit validation.

Coordinate-only edits never assign LocalTimestamp. Explicit eventUtc alone changes
the capture instant. Actual movement clears every obsolete address/provenance
member, optionally replaces authorized components, then overlays explicit Web
manual components. Manual input clears provider-only attribution/features; an
explicit null cannot be refilled by this command. Position/time derivation uses
the final locked pair and exact final instant; Notes/search use final content.
Actual position/time change invalidates observation admission without creating
presence/Visits. Equal position/time has no refresh/restamp side effect.

The first new keyed create alone mints OriginalCreateAddressToken. Its captured
token and pair are checked at contact admission and final ApplyToWinnerAsync.
Accepted explicit position/manual commands close it atomically even when content
is equal; successful backfill/repair publication also closes it. Normal create
no-result completion closes its matching token. Replay/restart/import never
re-arm or acquire the token. Failed/rolled-back commands claim no closure. Keep
consumed provider/attempt accounting; later independent enrichment uses its own
authority.

## Bulk, retirement and cleanup

Old PUT/DELETE, unconditional Web Edit POST and bare-ID bulk-delete payloads
refuse with 428 protectedMutationRequired. Genuine cookie/antiforgery bulk commands
require operationScope bulk, including a deliberate one-row selection. Notes uses
Apply-time owner/filter membership, ID-order row locks and final locked Notes for
append; replace/clear overwrite current values. Derived search and the selected
set are atomic. Selected bulk deletion targets then-existing owned IDs and commits
markers/dependents/deletion together. Preview is advisory. Neither tool has
single-action recovery or automatic uncertain replay. Frozen selections are not
implemented.

Receipts remain queryable until actual cleanup, with eligibility no earlier than
signed expiry + 30 days. One nonconcurrent Quartz job wakes every five minutes:
indexed pages/owner batches <=200, nonblocking account locks, one-second row-lock
and two-second statement timeouts, and no new transaction after 20 transactions or
10 seconds. Its final transaction may overrun. The durable scan cursor advances
past busy candidates. Budget exhaustion with backlog schedules at most one
one-minute follow-up. Periodic/admin scheduling handles outages. Markers are never
removed by this job. Cleanup eligibility, queryable attribution removal, vacuum,
reuse and filesystem reclamation are distinct; arrivals/busy owners/outages can
grow backlog and disk indefinitely. Defaults are not capacity guarantees and never
limit the age of phone captures, drafts, deletes or pending work.

## Outcome codes

| HTTP / code | Meaning |
| --- | --- |
| 200 applied | Correlated committed action/no-op; fresh detail is separate |
| 202 pending | Matching reserved first executor, future effects still possible |
| 409 locationBaseStale | Correlated selected-original rejection; stale group names only |
| 409 operationIdentityReused | This invocation differs; recover the original separately |
| 422 mutationInvalid / activityInvalid / baseUnavailable / baseNormalizationUnsupported | Atomic validation rejection; malformed/unscoped bodies cannot claim a receipt |
| 410 locationDeleted | Affirmative owner/key marker; does not attribute this action as deleter |
| 404 locationUnavailable | Nonleaking missing/foreign/keyless target; no absence attribution |
| 409 operationExecutionExpired | Fixed deadline rejection; no late executor publication |
| 410 operationExpired on POST | Execution forbidden; missing historical evidence stays Unknown |
| 200 unknown on GET | Observation with explicitly true/false/null future effects |
| 428 protectedMutationRequired | Retired unsafe route/body refused |
| 401 mutationAuthenticationRequired / 403 mutationForbidden or antiforgeryInvalid | Hold work; does not settle an older attempt |
| 429 mutationBusy / 503 mutationOutcomeUnknown / malformed, transport or 5xx | Preserve uncertainty without an independently valid terminal receipt |
