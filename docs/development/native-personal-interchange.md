# Personal native interchange (#789 WB2)

WB2 extends the existing native pipeline, rather than introducing synchronization,
receipts or a second planning schema. WB1's [typed value contract](trip-planning.md)
is shared by storage, owner reads and the personal file codec. Web
[Check plan (WB3)](trip-planning.md#optional-web-checks-wb3) keeps local setup out
of this format. Future cloning/versioning and mobile implementation are separate work.

## Formats and fidelity

| Download | Native version | Planning |
| --- | --- | --- |
| Public/default Wayfarer KML | 3 | No keys, null markers, counts or existence hints |
| Authenticated personal download, Include unchecked | 3 | Same redacted reusable content |
| Authenticated personal download, Include checked | 4, including an empty Trip | Complete typed aggregates on annotated objects |
| Google My Maps KML, PDF, other formats | Existing formats | Generic exclusion; no Include control or partial preservation claim |

V4 shares v3 parsing, mapping and reconciliation: exact `DistanceKm` doubles,
nonnegative `DurationTicks` in multiples of ten, automatic/manual provenance,
custom/fallback route state and From/To/ordered Via references, every Area ring
and hole, authored graph order (including null Place order), supported rich Notes,
tags and credential-free route/address provenance. Saved estimates are not
recalculated using destination profile speeds. Provenance identifiers remain
historical scalars, never credential or mutation authority.

Region Folders are identified by their native `RegionId`, independently of the
authored name. A Region named `Segments` stays a Region, including when empty or
alongside the transport container. Only a `Segments` Folder without Region
identity is the transport container; validation and parsing use that same rule
for child ownership. Malformed Region identity is rejected, never discarded.

Each annotated Document (Trip), Region Folder or Place/Area/Segment Placemark
has one XML-escaped `Data name="Planning"` value in ExtendedData. Its JSON is the
complete nonempty format-1 `PlanningAggregate` from `PlanningCodec`, retaining
entry IDs/order/repeated entries, literal labels and civil components, per-value
precision, optional meaning/allocation, absence, open bounds, explicit
unknown/flexible timing and supported zones/fold choices. Missing Data is omission.
JSON null, empty strings or empty collections are invalid, never Clear.

Example within a v4 identity-bearing node:

```xml
<Data name="Planning"><value>{"format":1,"entries":[{"id":"11111111-1111-4111-8111-111111111111","when":{"form":"at","value":{"precision":"month","local":"2027-03"}}}]}</value></Data>
```

Detection precedes generic fallback. Reserved Planning claims anywhere in
legacy/unversioned/generic content or at ignored/misplaced nodes reject. Unknown
native versions, unsupported v4 metadata/JSON members or enums, duplicate XML
values/keys, duplicate JSON keys (including case aliases), ambiguous graph IDs,
invalid references and invalid planning reject the entire file. New file values
use current zone rules; uploaded claims cannot obtain WB1's trusted historical
endpoint exemption. Stored values preserved from the current target retain that
exemption. No geography-only recovery is reported as success.

The existing XML ceiling is 10 Mi characters and encoded upload ceiling is four
bytes per character plus a UTF-32 BOM, additionally subject to configured upload
limits. Planning JSON is at most 262144 characters per aggregate, depth 16,
128 entries and 200 characters per label. Export fails rather than truncating
complete content that exceeds the supported budgets. Existing v1/v2/v3 imports
remain supported. The pinned v1–v3 reader rejects explicit version 4; third-party
KML viewers may display geography but are not supported round-trip tools.

Device-local check inputs, scopes, premises, buffers, findings, dismissals,
originals, review tokens and receipts never enter files. Unknown fields are not
stored opaquely. Native entry keys do not authorize joins against stored planning.

## HTTP and consumer workflow

These file actions use the existing browser cookie authority. They are not new
bearer upload endpoints. Mobile agents own any native UI/transport implementation
and qualification; unsupported file surfaces must be stated as unsupported.

| Action | Method and route | Authority |
| --- | --- | --- |
| Public/default native export | `GET /Trip/ExportWayfarerKml/{id}` | Public Trip or existing owner access; flags cannot upgrade to personal |
| Personal native export | `GET /Trip/ExportPersonalWayfarerKml/{id}?includePrivatePlanning=true` | Active authenticated User and exact ownership, including public Trips |
| Import or apply reviewed update | `POST /User/Trip/Import` | Cookie authentication, antiforgery and upload admission |
| Read replacement review | `POST /User/Trip/ReviewImport` | Same cookie, antiforgery and upload admission |

Omit `includePrivatePlanning` for personal v3. Native personal controls reset to
unchecked on every opening and successful download, and display the approved
downloaded-file disclosure. The personal service reads active authority and the
complete graph/planning in one repeatable-read snapshot. No posted owner ID is
accepted. The signed-in owner's public URL still uses the public representation.

Import uses multipart fields `file` and `mode` (`Auto`, `CreateNew`, `Upsert`).
Auto preserves the established duplicate-owned-Trip response:
`{ status: "duplicate", tripId }`. Otherwise it creates a new private Trip for the
active user. CreateNew always remaps graph and entry IDs and internal references;
it never hydrates another stored Trip's content by uploaded identities. Personal
v4 preserves the authored Trip name; legacy/native older-version naming retains
its established Imported suffix. Server write timestamps are new mutation metadata.

Every native Upsert requires review. Send the exact selected file to ReviewImport;
success is `{ status: "review", review }`. The review contains `token`, `tripId`,
`targetName`, `importedName` and `planning` rows. Each row has `key`, `kind`, `name`,
`state`, complete typed `current`/`imported` aggregates and signed `choices` with
`action`/`token`. States are `omitted`, `equal`, `absent`, `conflict`, `removed`
and `new`. Names label the preview and are never matching keys.

- Omitted planning preserves the whole current aggregate, including the Trip.
- Equal planning needs no conflict choice. An absent target accepts valid included
  planning against the reviewed absence. New targets receive fresh entry keys.
- Different existing planning defaults to Keep. Choose `keep`, `use` or explicit
  `clear` for the whole target; they are mutually exclusive. Included aggregates
  show all entries, making removed entries inspectable. Retained targets keep the
  selected valid entry keys. No implicit merge, union or date matching occurs.
- A removed/reidentified annotated target requires a selected `remove` permit.
  Cancel/CreateNew remains available. Matching by name/proximity is prohibited.

Apply with the same bytes and `mode=Upsert`, `reviewToken=review.token`, plus
repeated `choices` fields containing the selected signed permit per target. A
missing retained-target choice means Keep; absence of a removal permit refuses
loss. Duplicate/contrary choices reject. Do not send raw target IDs as permissions.
The Web retains the selected File and explicit choices on known failures, offers
fresh review, and never silently applies a refreshed review.

Review/choice permits expire after twenty minutes and bind the exact original
file bytes (including encoding/whitespace), Upsert mode, owner/target/type and
membership, all current reusable material and planning originals including
absence, and each explicit operation. Before applying, child/parent/Trip row
barriers fence current writers, then authority, membership, originals and permits
are revalidated after waits. Foreign or cross-type graph identity collisions
reject generically. Planning is applied/restored/cleared with graph replacement,
tags and measurement reconciliation in one relational transaction. A late failure
rolls back the entire operation. No separate pre-clear is allowed.

Successful import keeps the established `{ status: "success", tripId,
redirectUrl, notices }` response. Known refusals return safe JSON
`{ status: "error", code, message }`: malformed KML is 400, invalid content 422,
and `import_review_required`, `import_review_stale`, `import_choices_invalid` or
`import_loss_consent_required` are 409. Refresh a stale review and inspect it;
do not overwrite blindly. Recovered authorization failures do not grant file IDs
any authority. Cancellation before confirmed commit remains a refused operation;
a failed commit confirmation is explicitly uncertain.

A lost, redirected, malformed or unexpected mutating response, including
`import_outcome_unknown`, cannot establish rollback. The mounted Web disables
import/update/CreateNew and retains intent, with an owner-scoped tab-session
in-flight hold across reload. Check My Trips opens an inspection page; neither
that visit nor GET equality establishes the earlier outcome. **Abandon unresolved
attempt…** requires confirmation that the earlier import may have succeeded,
server work is not cancelled or rolled back, and repetition may create another
copy. Confirmation retires the local File, review and choices without resending
them. The earlier outcome stays unknown; a new file selection and deliberate
import action are required. Cancelling the confirmation preserves the hold.
Per-attempt local markers fence late replies from abandoned requests, without
adding receipts or automatic retry. The owner-scoped hold is limited to this tab
session, not other tabs/devices; closing the tab can lose it. A File is retained
in the mounted page; reload retains the hold, not the browser File object.

## Privacy and validation

Personal export and import/review routes are fenced before authentication/error
re-execution with `private, no-store`, no public ETag/Last-Modified, and no shared
output/proxy/generated-file cache path. Personal downloads use browser no-store
fetch with no HEAD probe, progress channel or iframe fallback. No service-worker
cache exists in this path. Proxies/CDNs must honor the private no-store routes.
Subsequent public downloads retain the same public bytes/headers/validators.

Focused qualification: `NativeKmlV4Tests`, `NativePlanningPostgresTests`, affected
legacy/native import/export and security inventory tests, `tripImportClient` state
tests and two concise managed Chromium control observations in
`nativePlanning.spec.ts`. Use the persistent guarded PostgreSQL 18/PostGIS test
database. WB1 value/editor/proof acceptance remains reused at its recorded scope;
WB2 is not WB3, mobile, deployment or final combined adversarial acceptance.
