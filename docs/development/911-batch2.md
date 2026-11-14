# Trip construction, saved composition and selective apply

Contract: [#911](https://github.com/stef-k/Wayfarer/issues/911), decoded UTF-8 body
SHA-256 `4d4ea3b4e0bafb9237fc319d9dd2303b279cad355d13306df26cd2b4f03b81b6`.
The assignment's initial pin was `3393c7f4ba24b2617b4e0a9328ea61c86ce725b2ecfef189c316331c7aecaeb2`;
the 14 November review-only amendment leaves the five implementation scopes unchanged.
Backend construction and selective-apply reference. Product decisions and acceptance
remain in the tracker. Web Layout B/My Trips consumes the same domain owners below.
Lightweight existing-Trip copy and mobile implementation remain separate scopes.

## Routes and authority

All responses are owner-private/no-store. Cookie routes require the User role;
POSTs require antiforgery. Bearer routes ignore cookie identity and resolve the
incoming API token, then revalidate it inside the service transaction after waits.
The exhaustive production mutation inventory classifies every cookie and bearer
command; list/read/candidate GETs remain outside the unsafe-action inventory.

| Operation | Cookie route | Bearer route |
| --- | --- | --- |
| New Version | `POST /api/trip-compositions/versions` | `POST /api/trips/versions` |
| Start | `POST /api/trip-compositions` | `POST /api/trips/compositions` |
| List unfinished | `GET /api/trip-compositions` | `GET /api/trips/compositions` |
| Resume | `GET /api/trip-compositions/{id}` | `GET /api/trips/compositions/{id}` |
| Finish | `POST /api/trip-compositions/{id}/finish` | `POST /api/trips/compositions/{id}/finish` |
| Suggestions/search | `GET /api/trip-compositions/{id}/candidates` | `GET /api/trips/compositions/{id}/candidates` |
| Selected action | `POST /api/trip-compositions/{id}/actions` | `POST /api/trips/compositions/{id}/actions` |

Web discovery adds `GET /api/trip-compositions/sources` with a name search and
`ownedOnly` filter. Its bounded 20-row projection contains only ID, name, owned
flag and contribution policy, after active-owner/eligibility checks. No planning,
diagnostics, account objects or graph content is returned. Construction remains
the acquisition authority. `/User/Trip/Compose/{id}` is a private shell; it has no
private source bootstrap data and every API read/action reauthorizes the workspace.

Version and Start accept `sourceA`, optional `name` (1–200 characters), optional
`changeNote` (at most 2000 characters), and `keepOwnBasePlanning` (default true).
Start additionally requires `sourceB`. Version always copies only A.

```json
{
  "sourceA": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
  "sourceB": "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
  "name": "Independent itinerary",
  "keepOwnBasePlanning": true
}
```

Confirmed construction returns `tripId`, nullable `compositionId`, `status`
(`created` or `inProgress`), `mediaOmitted` and the saved `current` graph with own planning. List accepts optional `sourceA` and
`sourceB` filters for the ordered pair and returns composition/C identities, current
C name and creation time. Resume reads the selected saved workspace; it never calls
Start. Explicit Start always requests a new C. Clients must offer Continue Existing
versus deliberate Start New and must not blindly retry an unconfirmed creation.
There is no request-key deduplication or exactly-once promise.

A must currently belong to the active caller. B may belong to that caller, or be
public with an active owner and an explicit BY/CC0 reuse grant. Private, view-only,
legacy unlicensed and inactive-owner foreign sources refuse without capture.
Unavailable authority returns `composition-unavailable`; Finish/read of a completed
workspace returns `composition-closed`. Creation contention returns a normal 409.
Connection failures must not be presented as proof of rollback: inspect or refresh
Trips before another creation. Existing ordinary editor mutations retain their
field-original, draft and immutable-attempt protections.

## Construction and captured content

`TripCloneCoordinator` owns the shared direct graph builder and transaction-neutral
persistence. It supplies fresh graph/entry identities, parent/endpoint/Via mapping,
existing catalogue tags, eligible media, full polygons, precision and provenance.
Version/Start retain own planning by default; false clears it only in C. Existing
foreign Clone remains planning-redacted. Own-source copies retain existing credits
without automatically publishing private source names as newly generated credits.
B's content and notices remain captured comparison inputs, not adopted C content.

Source capture is RepeatableRead, with participating account deletion fences in
stable owner order, active-account locks, profile barriers before source Trip locks,
and final ownership/reuse/credential checks. Source changes after capture never
rewrite saved inputs or C. Invalid custom endpoint compatibility refuses; the
builder never snaps or regenerates a route. New parents/Places are flushed before
the shared route staging core queries them. Trusted saved measurements/provenance
are installed only after complete compatibility validation. Intermediate saves
remain inside the one outer transaction, including workspace/origins and credits.
After validated route staging, copy persistence restores already-normalized Segment
Notes alongside planning/provenance, preserving null separately from empty and
nonempty HTML for both owned construction and existing foreign Clone. Ordinary
new-route Notes defaults remain unchanged; source records are never rewritten.

Each new destination list receives distinct nonnegative order values in source
editor sequence: Regions/Segments by order then ID, Places with null order last
then ID, Areas by order then name then ID. Canonical Unassigned retains its reserved
identity and source sequence position; it is not renamed or duplicated. Source
orders and unrelated lists are unchanged. Selective placement is described below.

Snapshots use a versioned explicit graph projection, raw stored routes/measurements,
and separate sequence vectors. Authorized own planning has a separate projection;
foreign planning has no property, placeholder, count, diagnostics or presence hint.
The snapshot retains a `mediaOmitted` indication for later reselection.
No account objects, credentials, visits, progress-sharing state or private origins
enter this projection. Batch-1 media omission applies before foreign snapshotting.
Resume returns immutable `sourceA`/`sourceB`, type-scoped A/B `mappings` and
current C with own planning. Old initial A mappings remain `included`. Missing C
identities become durable `destinationRemoved` decisions on resume or apply;
retained scalar mappings survive deletions and no read reconstructs content.

## Matching and durable selections

Candidate query parameters are `source=a|b`, `kind=region|place|area|segment`,
`sourceId` and optional `search` (at most 200 characters). Responses contain at most
20 deterministic `possibleMatch` suggestions with current C IDs and evidence:
name/context and distance, mapped child distribution, full polygon relationships,
or mapped ordered anchors/mode/profile/route shape. Neither a score, equal name,
950-metre separation, overlap nor common endpoints confirms identity. Planning is
not matching evidence. This is a result-count bound: the current graph is materialized
and evidence (including polygon topology) is computed before truncation. There is
no fixed total-work bound. Manual search filters names before evidence; manual reuse can choose another current same-type C ID. Place/Area reuse also
supplies `expectedParentId` from the reviewed current counterpart; it never moves it.

Actions accept one `source` and 1–128 reviewed `items` including Region children.
Each item has `kind`, `sourceId`, `decision=copy|reuse|skip|remove` and, when already
resolved, `expectedDecision: {destinationId,decision}` from resume. Decisions are
`included`, `reuse`, `separate`, `skip` or `destinationRemoved`, scoped by source
and kind. Active additional copies retain their own counterparts, including the initial A
identity. Re-add replaces its removed choice; no per-action history accumulates.
`separate:true` explicitly approves a same-name/additional distinct copy.
`readd:true` is required to deliberately copy/reuse an excluded or removed idea.
Skip does not silently delete an already included counterpart: use reviewed Remove.

Copy receives fresh IDs. Optional `name` selects a validated custom name/title;
omission uses the captured original, preserving null Segment titles. Place/Area
copies select `regionId` in current C. A Place with no Region uses the unique
canonical Unassigned category; an Area must select a normal Region or specify
`newRegionName` and its `regionPlacement`. `expectedRegions` carries current
`{id,displayOrder}` originals for canonical lookup/creation or explicit normal
Region creation. No source Region ID authorizes a destination.

A Region item with omitted `children` copies only normal metadata. To copy children,
provide every direct Place/Area once with an explicit copy/reuse/skip decision,
in reviewed order; no Segment is implicit. Reused children retain their current
containers. Canonical Unassigned is not a normal Region to duplicate: copy its
selected Places to C's canonical category instead.

Every new list block carries `placement: {expected, position, afterId}`. `position`
is `append` (default), `start` or `after`; only `after` has a reference.
`expected` is the full affected current list of `{id,displayOrder}` in displayed
sequence, including exact nullable/tied raw orders. Use current `regionOrder`,
`placeOrder`, `areaOrder` and Segment display sequence from resume. Normal Region
placement excludes canonical Unassigned. A newly created empty parent uses `[]`.
All new siblings in the same list use the same placement and enter contiguously in
item order. Reused items stay put. Reference/type/parent/membership/order drift
refuses atomically; no fallback append or container change occurs. Existing C
counterpart IDs may be resolved explicitly from confirmed mappings; a source row
alone supplies no position. Normalize only affected lists, preserving geography,
planning and route/Via order.

```json
{
  "source": "b",
  "items": [{
    "kind": "place",
    "sourceId": "11111111-1111-1111-1111-111111111111",
    "decision": "reuse",
    "destinationId": "22222222-2222-2222-2222-222222222222",
    "expectedParentId": "33333333-3333-3333-3333-333333333333",
    "fields": { "notes": { "operation": "useSource", "expected": null } }
  }]
}
```

## Selected fields and domain consequences

`fields` has only named groups: `name`, independent nullable Segment `title`, whole
`notes`, `iconName`, `markerColor`, `fillHex`, `cover`, coherent `address`, complete
`location`/`geometry`/`center`, whole `route`, whole `planning`, Trip `tags` and
`viewport`. Omission keeps the field. Each selected group uses
`{operation:"useSource"|"edit"|"clear", expected, value?}`. Matched targets compare
only selected current originals, including explicit nulls, after waits. A stale
selected group rolls back the entire action; disjoint edits survive. New copies
may select fields without destination originals. Required names/geometry cannot
be cleared. Notes replace as one normalized rich value; never concatenate HTML.

Originals use the camel-case captured/current field shapes: coordinate pairs are
`[lon,lat]`; Area geometry is the complete GeoJSON string; address is
`{address,resolvedFeatureName,resolvedFeatureType,addressEnrichmentProvider,
addressEnrichmentStorageMode,addressEnrichedAt}` with explicit nulls. Only Use source
may carry captured enrichment authority. Authored address edits accept `{address}`
and clear provider evidence. Cover is a nullable raw authorized HTTP(S) reference.
Viewport is `{centerLat,centerLon,zoom}`. Tag originals are sorted slugs; Use source
reconciles captured tag names/slugs with the current catalogue, while Edit accepts
validated tag names (up to 25). No source tag ID is destination authority.

Planning is the existing complete ordered aggregate. New B copies default absent;
own B Use source is explicit and remaps entry UUIDs. Matched Keep preserves current
entry keys. Foreign snapshots contain no planning, diagnostics or presence hints;
unavailable selected groups receive generic validation. Importing a child does not
adopt B's Trip Notes/tags/cover/viewport. Applicable captured credits accompany copied
or adopted content, including readable title-only notices. Own-source names are not
automatically public credits; existing inherited notices are retained on removal.
Foreign unqualified covers/Notes images remain omitted with `mediaOmitted` feedback.
No fetching, rehosting or proxy permission inference occurs during apply.

Route Use source requires explicit `anchors`, one per distinct captured From/To/Via
Place: `{sourceId,destinationId,regionId,location}`. Existing destinations include
exact current parent/location originals. `destinationId:null` refers only to an
explicit earlier copied Place in this action, shared across routes by source ID.
Source coordinates never authorize current C movement. Every custom present endpoint
is checked even with zero Via; Via identities, contiguous positions and strictly
increasing interior indices are validated. Custom tolerance is 0.0000001 degrees;
fallback coordinates and incomplete/null state must agree exactly. Incompatible
routes refuse with a correction path, without snapping, conversion or regeneration.
Compatible copies retain exact saved distance, duration ticks/source and provenance.

Matched route originals use the complete current route/dependency/measurement group,
excluding independent Notes/title/planning. `conditionToken` is the existing owner
editor `originalRouteToken`; destructive dependency changes also supply the existing
`routeClearToken` from ordinary route review. Route Edit accepts only
`{fromPlaceId,toPlaceId,waypoints,routeJson,mode,transportProfileId,durationSource,
manualDurationMinutes}` and uses ordinary reconciliation, never client provider or
trusted measurement assertions. Route Clear selects fallback with current ordered
anchors; it does not silently move Places. Edit/Clear also approve each current
anchor with exact originals. An unchanged authored route uses the ordinary editor's
server-owned measurement preservation, including exact distance/ticks and qualified
provider duration. Intentional duration/source or route changes reconcile normally.
A consent approving custom-route clearing clears geometry and Via vertex indices
for authored edits. Source adoption requiring that effect refuses: clear/repair
with ordinary controls and review again. It never converts a captured custom route
to fallback. Route facts and nested waypoint members are strictly validated once
before profile discovery; missing required facts, null entries and unknown members
are ordinary 400 refusals. No refusal renews consent.

Existing Place location adoption uses the ordinary coordinate prepare/condition
proof for exactly the selected coordinates in `conditionToken`. The same domain
core owns affected routes and enrichment consequences. Region/Place removal uses
the existing deletion preview token in `conditionToken`; Region also supplies
explicit `places=keep|delete` and `areas=keep|delete`. Leaf removal supplies `original`
with complete `content` (Area API DTO or Segment route group), `planning`, normalized
`notes` and nullable `title`. Removal retains all still-applicable Trip notices.

Selective apply joins account → workspace → discovered profiles → Segments → Places
→ Areas → Regions → Trip barriers in one Serializable transaction. It validates
active caller/credential, owned C, active workspace, originals and dependencies after
waits. Expanded earlier lock scope aborts for ordinary review. Approved dependencies
are saved before route staging, inside the same transaction. Lifecycle helpers use
narrow transaction-neutral cores; pending changes are flushed before tracker clearing,
then current C is reloaded. Content, dependencies, affected ordering, credits and
A/B decisions commit once. No independent endpoint calls or live-source reacquisition
occur. Finish/Delete/account erasure use the same workspace lifetime boundary.
Public content advances ordinary freshness and post-commit thumbnail/cache effects;
private planning/decision-only changes do not. No communication error proves rollback
or authorizes blind replay.

## Lifecycle, persistence and rollback

`AddTripCompositionLifecycle` adds `TripCompositions` and `TripOrigins`; it performs
no backfill and changes no old Trip or licence. A workspace has a unique C FK and
owner FK, both cascading on deletion. Historical A/B/object identities are scalars,
never source FKs. Origins contain only source Trip identity, time and optional
owner-private change note. C owns origins through a cascading FK.

Finish acquires the existing account fence, then the workspace barrier and current
C barrier. It atomically sets completion and clears both snapshots, mappings and
source-pair hints. The database check rejects completed rows retaining comparison
payloads. C fields, visibility, planning, credits, origins and public UpdatedAt
remain unchanged. A completed comparison cannot reopen.

Normal Web Trip deletion joins the same workspace barrier before root removal;
FK cascades delete workspace/origins with C. Existing account deletion coordination
holds the account fence and erases owned Trips/workspaces; another owner's sources,
independent copies and readable credits survive. Source deletion leaves authorized
snapshots intact. No Abandon, expiry, automatic purge or media deletion is added.

An unfinished C can be published and edited through ordinary routes. Public reads
continue to project only saved C; private workspace/planning/origin changes do not
advance public freshness. Workspace rows have no Trip navigation exposed by public
DTOs or native exports. Native files never restore comparison sessions.

Migration Down drops both new tables, losing comparisons/origins while retaining C,
its content and credits. Back up before rollback; an old binary cannot resume these
comparisons. Do not reconstruct them from public content or infer a completed state
from missing records.

## Validation boundaries

`TripCompositionPostgresTests` covers independent construction, planning choice,
policy/privacy, complete staged routes, rollback, resume/current edits, source loss,
Finish/deletion/account cascades and bounded acquisition/lifecycle interleavings.
Affected existing Clone, canonical Region, portable-field and lifecycle tests cover
shared callers. The delivery receipt records exact selections, failures, corrections,
Code Guard disposition and hosted CI startup separately.
The focused shared-copy relational regression reloads raw values through fresh EF
contexts for null, empty and sanitized nonempty Notes in both callers, retaining
source immutability, identity/Via remapping, geometry, measurements and provenance.

Backend tests alone do not qualify the rendered Layout B journey. Mobile must
adopt the documented bearer requests and complete graph,
sequence, precision, private planning and credit representations with account-scoped
storage and honest uncertain-save handling. Mobile UI/native qualification remains
separately owned. Independent technical acceptance and final combined backend/Web
reconciliation are recorded in #911; backend checks do not establish a full UI journey.

## Web ownership and ordinary editor integration

The existing Vue entry selects the ordinary editor or Layout B. Both share theme,
Leaflet/tile behavior, confirmation dialogs, rich Notes and ordinary domain editing.
The comparison drawer occupies its own layout box; map fitting uses the actual
positive map dimensions, with bounded padding. Complete polygon rings and route
anchors are retained. Keyboard/pointer drawer resizing shares the editor's viewport
budget and cancels on Escape; the compact layout shows one drawer section at a time.

`compositionModel` freezes the named selected field, decision, parent and list
originals from a reviewed baseline. `compositionState` owns the mounted comparison,
immutable submitted attempt and newer local choices. It rejects wrong-context replies
and read generations; a delayed acknowledgement updates saved C without resetting
newer input or closing another inspector. Stale/refused/unconfirmed actions retain
choices for explicit refresh/review, never replay. No outcome ledger or global
revision is introduced.
Retiring a read generation also releases its loading ownership. Apply's preparation
read uses the same complete workspace/context validator as Refresh, before publishing
the reply, obtaining previews or dispatching a mutation; invalid replies retain the
last usable comparison and choices.

Only the local view/drafts are retained under an origin/account/comparison key.
The retained baseline is owned C, not A/B snapshots; consent tokens and HTTP outcomes
are excluded. The server owns durable snapshots, decisions and C. Foreign planning
does not enter fields, labels, diagnostics, storage, candidate evidence or requests.
RichNotes adoption advances only for an explicit owner replacement, never a local
echo or an older save reply.
The choice object identifies the editing owner: switching between a selected item
and its collected block adopts that owner's Notes, and applying a block retains the
independent item draft. Retained reviewed originals are labelled separately from
fresh current-C values; refresh/edit-return flags changes without renewing originals
or replacing authored choices. Explicit originals review remains the consent boundary.

Each apply rereads authoritative C while retaining the submitted originals until
the user explicitly reviews a new baseline. Place/Region removal obtains existing
deletion previews, coordinate adoption obtains the coordinate-purpose proof, and
route adoption obtains current editor `originalRouteToken` plus complete selected
route facts and anchor approvals. Source adoption requiring custom-route clearing
is refused by the domain. Ordinary route editing owns original/clear-consequence
tokens, repair and measurements; it is linked with a validated same-owner/same-C
comparison return URL. Polygon and full planning editing also use ordinary controls.
There is no alternate rich editor, route adaptation or historical-format adapter.
The encoder indexes explicit copied Places through Region children as well as
top-level choices, using the effective forced parent. It stages those copies before
Trip-level Segments, shares each approved dependency, and refuses conflicting
destination approvals. Server source uniqueness, list placement and atomicity remain
unchanged. A newly copied normal Region has no existing destination ID for a Segment
approval; review that Region first, then refresh and approve its saved destination.

The coordinate-only cookie preview is the small integration addition: it checks
owned Trip/Region/Place scope before parsing, requires antiforgery, is private/no-store,
and calls `PlaceRegionLifecycleService.PreparePlaceStructureAsync` with operation
`coordinates` and the existing purpose-separated proof. It cannot grant a Web-update,
move or different coordinate effect. The unsafe-action inventory explicitly classifies
this read-only POST as a cookie/antiforgery contract. New routed tests exercise
discovery privacy/eligibility and exact preview-to-composition-apply intent.

My Trips reads unfinished work independently of ordinary list pagination. Existing
source pairs are checked again before deliberate Start New. Finish confirms permanent
comparison cleanup; normal native form submission owns Delete C's redirects/alerts.
The editor shell validates a supplied composition against the caller, destination
and active workspace before emitting its return link. Public readers, contribution
policy, credits and freshness remain with their existing owners.
Only a complete Finish reply bound to C releases the workspace and local drafts.
An incomplete or mismatched reply explicitly reports an unconfirmed Finish and
retains the last usable comparison for inspection, without replay or an outcome registry.
