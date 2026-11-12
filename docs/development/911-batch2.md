# #911 Batch 2: construction and composition lifecycle

Contract: [#911](https://github.com/stef-k/Wayfarer/issues/911), decoded UTF-8 body
SHA-256 `3393c7f4ba24b2617b4e0a9328ea61c86ce725b2ecfef189c316331c7aecaeb2`.
Base: `0f66cd49c7d30060371482624e52d47550be4cbb`, accepted Batch 1 / PR #975.
This slice supplies backend foundations. Matching/adoption, Layout B/My Trips,
existing-Trip copy and mobile implementation remain separately owned later work.

## Routes and authority

All responses are owner-private/no-store. Cookie routes require the User role;
POSTs require antiforgery. Bearer routes ignore cookie identity and resolve the
incoming API token, then revalidate it inside the service transaction after waits.

| Operation | Cookie route | Bearer route |
| --- | --- | --- |
| New Version | `POST /api/trip-compositions/versions` | `POST /api/trips/versions` |
| Start | `POST /api/trip-compositions` | `POST /api/trips/compositions` |
| List unfinished | `GET /api/trip-compositions` | `GET /api/trips/compositions` |
| Resume | `GET /api/trip-compositions/{id}` | `GET /api/trips/compositions/{id}` |
| Finish | `POST /api/trip-compositions/{id}/finish` | `POST /api/trips/compositions/{id}/finish` |

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

Each new destination list receives distinct nonnegative order values in source
editor sequence: Regions/Segments by order then ID, Places with null order last
then ID, Areas by order then name then ID. Canonical Unassigned retains its reserved
identity and source sequence position; it is not renamed or duplicated. Source
orders and unrelated lists are unchanged. Later interactive placement is Batch 3.

Snapshots use a versioned explicit graph projection, raw stored routes/measurements,
and separate sequence vectors. Authorized own planning has a separate projection;
foreign planning has no property, placeholder, count, diagnostics or presence hint.
No account objects, credentials, visits, progress-sharing state or private origins
enter this projection. Batch-1 media omission applies before foreign snapshotting.
Resume returns immutable `sourceA`/`sourceB`, initial source-to-C `mappings` and
current C with own planning. Missing C identities report `destinationRemoved`;
retained scalar mappings survive deletions and no read reconstructs content.
There is no per-action history or premature matching engine.

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

## Evidence and remaining ownership

`TripCompositionPostgresTests` covers independent construction, planning choice,
policy/privacy, complete staged routes, rollback, resume/current edits, source loss,
Finish/deletion/account cascades and bounded acquisition/lifecycle interleavings.
Affected existing Clone, canonical Region, portable-field and lifecycle tests cover
shared callers. The delivery receipt records exact selections, failures, corrections,
Code Guard disposition and hosted CI startup separately.

No browser campaign is needed for these backend routes; no Layout B journey is
claimed. Mobile must adopt the documented bearer requests and complete graph,
sequence, precision, private planning and credit representations with account-scoped
storage and honest uncertain-save handling. Mobile UI/native qualification remains
pending. Independent Batch-2 review and final combined backend/Web reconciliation
remain required; this implementation does not accept itself or close #911.
