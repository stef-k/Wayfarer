---
title: Option C Activity reconciliation contract
---

# Option C Activity reconciliation contract

**Status: CORRECTED DESIGN READY for fresh independent review.**
This is a proposed implementation contract, not shipped behavior or authorization
to implement. It belongs to [#814][issue] under [#805][parent]. The maintainer's
Option C selection supersedes name-only lookup and historical-label-only designs.
The live #814 migration exclusion remains in force until explicitly amended.
This revision addresses the [independent review][design-review] and incorporates
the maintainer's approved decisions: automatic creation without creation quotas,
case-insensitive matching with surrounding trim only, and an authenticated catalog
endpoint compatible with existing Mobile clients. Implementation remains separate.

## 1. Evidence and current behavior

Source baseline: `9d8ee6b4132dfef53beb167b86b282be31c88973`, live `main` verified
on 2026-10-12. Read the live [repository rules][agents] and issues before using
this contract against another commit. #811, #812 and #813 are already merged;
preserve their execution fencing, CSV/GeoJSON UUID fidelity and timestamp rules.

The completed #814 investigation is the evidence baseline, reused without new
product experiments. Its retained directory is
`/tmp/wayfarer-814-20261012-7e1edvzo/`. `observations.json` has SHA-256
`8051d612da8699a096157a36e2282a3283fdf55e3d29bee4b48d5db624be63cc`.
It reports PostgreSQL 18.6/PostGIS 3.6.4 qualification, 25 existing focused tests,
one relational observation and one CSV optional-string observation passing.
Those are prior local witnesses, not validation of this proposed mechanism.
The [original independent metadata finding][metadata-evidence] remains relevant.

| Current fact | Source and consequence |
| --- | --- |
| Activity has an integer ID, required unrestricted text Name and nullable text Description. | [Model][activity-model], [initial schema][initial-activity]; no name uniqueness or length validation. |
| Bundled Activities are seeded only when the table is empty. | [Seeder][seed]; reference data, not a permanent allowlist or synchronization source. |
| Locations hold a nullable Activity FK; parsed names are transient. | [Location][location-model], [import metadata][import-metadata]; an unmatched name is not retained in the database. |
| Worker loads the whole catalog into an ordinal-ignore-case dictionary before Location deduplication. Only the imported name is trimmed. | [Worker][worker-resolution]; missing names remain unclassified; duplicates can throw even when unrelated to the requested name. |
| Accepted rows, duplicate counts and checkpoints share a PostgreSQL transaction. Keyed conflicts are recovered through savepoints. | [Batch transaction][worker-batch], [deduplicator][deduplicator]; preserve these boundaries and owner/UUID first-writer behavior. |
| Fresh authenticated exports do not load Activity; all four pairs omit LocationType. | [Owner query/CSV/GeoJSON][export-query], [GPX][export-gpx], [KML][export-kml], [parsers][csv-parser]; tracking fixup can conceal the omission. |
| The deployed Activity FK uses NO ACTION. Admin deletion does not translate reference failures. | [FK migration][activity-fk], [CRUD][admin-crud]; investigation observed SQLSTATE 23503 with both references preserved. |
| GET /api/activity currently exposes the shared Name/Description catalog anonymously. | [Endpoint][activity-api], [base authentication helpers][api-auth]; the admission filter does not authenticate an action that never resolves credentials. |
| Mobile sync already uses Bearer tokens and caches integer Activity IDs; offline writes retain those IDs. | [ActivitySyncService][mobile-sync], [queued check-ins][mobile-queue], [queued updates][mobile-updates]; deleting duplicate IDs requires backend compatibility beyond a cache refresh. |
| CSV's current conventional encoding conflates null and zero-length strings. | Retained `OptionalStringCsvObservation.cs`; nonempty values, including surrounding spaces, survive. |

The investigation also observed a 51-row import retain its first 50-row checkpoint
when duplicate catalog names failed the labelled tail. This motivates fixing the
catalog prerequisite; it does not justify replacing Quartz or import recovery.

## 2. Authoritative name normalization

Introduce one migration-owned PostgreSQL function:
`public.wayfarer_activity_name_v1(raw text, identity boolean) RETURNS text`.
It is immutable, strict, uses explicit schema/collation references, and has no
database lookups, configuration dependencies or privilege elevation. Both modes
perform the entire validation algorithm; false returns display text, true returns
the identity key. Null input yields null; an invalid non-null input yields null;
a valid whitespace-only input yields the empty string, meaning missing.

The algorithm, in order:

1. Reject NUL and malformed Unicode before binding PostgreSQL text; .NET must use
   strict encoding, not replacement characters. This transport guard performs no
   name comparison or normalization.
2. Trim Unicode 16.0 `White_Space` scalars **only from the two ends**. The set is
   U+0009–000D, 0020, 0085, 00A0, 1680, 2000–200A, 2028–2029, 202F, 205F and 3000.
   Preserve every interior scalar, including repeated spaces, NBSP, tabs and
   line breaks. Do not replace separators or collapse runs.
3. Reject only remaining characters forbidden in [XML 1.0 text][xml-characters]:
   U+0001–0008, 000B–000C, 000E–001F, FFFE and FFFF. This small storage/fidelity
   check prevents a newly accepted name breaking native GPX/KML export. Interior
   tab, LF and CR remain valid and significant. No Unicode property blacklist,
   default-ignorable stripping or script/emoji exception registry is introduced.
4. Normalize display to **NFC**, preserving case, accents, punctuation, acronyms
   and intentional mixed capitalization. Empty means missing. Compute the key as
   `normalize(casefold(display COLLATE pg_catalog.pg_unicode_fast), NFC)`.
   Return display or key according to the mode; never truncate either.

Embed only the fixed surrounding-whitespace set and XML-invalid ranges in this
function. The pinned [Unicode whitespace data][unicode-space] defines the trim
set. Do not infer matching rules from the host locale, a mutable lookup table or
a second .NET Unicode implementation. Join controls, variation selectors, format
characters and zero-width spaces remain significant when permitted by XML.

Remove the proposed raw-byte, display-scalar and key-length ceilings. A valid
legacy name is not invalid because it exceeds 100 characters or 1,024 raw bytes.
Keep unrestricted text columns and the [existing upload/request ceiling][upload-policy],
bounded batches and database deadlines. Qualify the actual full-key index against
legacy data before deployment: PostgreSQL's physical index/storage capacity can
still reject an unusually large key. Report that real resource failure without
truncating, broadening identity, or committing a valid Location without its Activity.

PostgreSQL supplies [NFC and full case folding][pg-strings]. Its built-in
[pg_unicode_fast collation][pg-collation] is independent of the host locale and
stable within a PostgreSQL major version. This choice uses the maintained PG18
baseline and needs no ICU extension. A future major upgrade must qualify the
vectors below and deliberately rebuild/reconcile keys if semantics change.
Never change v1 in place while stored generated keys still use its old output.

The .NET wrapper calls the function for both outputs, batched for import rows.
It uses [StringComparer.Ordinal][dotnet-ordinal] only to compare returned keys.
Database key equality uses deterministic `C` collation. Equality therefore agrees
without relying on `OrdinalIgnoreCase`, `ToLowerInvariant`, `citext`, `ILIKE` or
locale-dependent SQL `lower`. PostgreSQL orders locks by key with `COLLATE "C"`;
do not assume UTF-16 ordinal ordering matches UTF-8 byte ordering.

| Inputs | Identity/display result |
| --- | --- |
| Walking, walking, WALKING, wAlKiNg, ` Walking ` | One key, `walking`; existing display wins. |
| ` Eating Out `, `\tEaTiNg Out\n`, surrounding NBSP + `Eating Out` | One key, `eating out`; trim only the surrounding whitespace. |
| `Eating Out`, `Eating  Out`, `Eating\tOut`, `Eating Out` (interior NBSP) | Four distinct keys; preserve interior spelling in display and identity. |
| `Πεζοπορία`, `ΠΕΖΟΠΟΡΊΑ`, equivalent decomposed tonos | One accented Greek identity; NFC display retains supplied case. |
| `ΟΣ`, `ος`, `οσ`; `Straße`, `STRASSE` | Respectively one identity through full Unicode folding, including final sigma and ß. |
| `GPS`, `eBike` | Preserve supplied display; do not title-case. |
| Walking versus Hiking; accented versus unaccented Greek; Latin A versus Greek Α | Different keys; no semantic, accent-removal or confusable matching. |
| I/i versus dotless ı; dotted İ versus i | Default Unicode folding, not Turkish locale rules; dotless and dotted variants remain distinct from plain i. |
| Null, empty, only accepted whitespace | Missing; no Activity. |
| Malformed Unicode, NUL, interior U+0001 or U+FFFE | Invalid; never create or rewrite into another identity. |
| Script join controls, emoji variation selectors, zero-width space within a name, ordinary 101-character name | Valid when XML/storage compatible; retain significant scalars and display. |

Within a file, the first accepted occurrence supplies a missing entry's display.
Across concurrent transactions, the first committed creator establishes display;
later imports never rename it. This is explicit first-writer authority, not an
arrival-order-independent choice of capitalization or integer ID. All contenders
converge to the same persisted identity and FK. Administrators can change display.

## 3. Deterministic behavior matrix

Apply the rows below after Location duplicate arbitration. Description differences
never change name identity. Missing and invalid classifications can commit with a
null FK; failure to reconcile a valid classification cannot.

| Condition | Catalog/resulting new Location | Existing Locations and reported outcome |
| --- | --- | --- |
| Unique existing valid key | Reuse destination ID; retain destination display/description. | No existing row mutation. |
| Missing valid key | Automatically create with canonical display and null Description; link atomically. | No per-import/account creation allowance or spending counter. |
| Identity-equivalent existing duplicates | Deployment cleanup selects lowest ID and moves all references. | Preserve descriptions and removed numeric IDs through the compatibility map below. |
| Unexpected duplicates after prerequisite deployment | Schema/configuration failure; rollback current batch and fail execution. | Keep prior prefix/staged file; never choose an arbitrary row. |
| Different normalized names | Keep separate entries, even with identical descriptions. | Walking/Hiking never merge. |
| Activity renamed Walking → Hiking; later import says Walking | Reuse a currently existing Walking or create a separate one. | Old Locations keep the renamed entry's FK. |
| Missing Activity | Null FK; no catalog work or warning. | Location is accepted unclassified. |
| Invalid Activity, including non-string JSON value | Null FK; count one unresolved classification. | Accept Location and metadata; show Invalid warning. |
| Catalog contention, timeout or winner-resolution race for a valid key | Retry within bounded batch policy; if unresolved, roll back the whole current batch. | No Location/checkpoint/warning advance; retain prior prefix for restart/resume. |
| Resource/constraint/schema failure while resolving a valid key | Roll back current batch and fail using the existing lifecycle. | No permanent classification loss; report safe failure and retain staged-file authority. |
| Duplicate Location, including late UUID race loser | No reconciliation/creation for discarded row. | Existing classification and all other metadata stay unchanged. |
| Retry of committed batch or already accepted unclassified Location | Skip existing Location using existing identity rules. | Do not fill or overwrite classification as a replay side effect. |
| Cancel/stale epoch or unrecoverable database failure | Roll back uncommitted Locations, creations, invalid count and checkpoint. | Retain committed prefix; use existing cancellation/failure lifecycle. |
| API write with a removed duplicate Activity ID | Resolve the numeric compatibility map to the survivor before saving its FK. | Existing check-in/update payload and auth remain valid; no mobile upgrade required. |

Physical Location deletion still releases its UUID reservation; subsequent replay
can insert again under the existing contract. Legacy keyless matching remains
best-effort, not a new guarantee of universal duplicate detection.

## 4. Data model and one-time reconciliation

Keep `ActivityType`, `Location.ActivityTypeId`, `Name` and text `Description`.
Add `ActivityTypes.NormalizedName`: a **stored generated**, non-null text column
with `C` collation, generated by v1 in identity mode. Map it as a read-only EF
shadow property so it does not expand entity responses or portable history files.
[Stored generation][pg-generated] prevents callers supplying a mismatching key.

Add a named unique index `IX_ActivityTypes_NormalizedName` and a check requiring
`Name` to equal v1's display output under `C`, and to be nonempty. Make the check
explicitly `IS TRUE`: SQL null must not let invalid names pass. The generated
column's NOT NULL and validation also reject invalid keys. Application prechecks
improve messages; the database arbitrates every insertion and rename.
Retain the database FK's **NO ACTION, NOT DEFERRABLE** protection and configure EF
as `DeleteBehavior.ClientNoAction`. EF's `Restrict`/`NoAction` settings can still
null tracked optional dependents; [ClientNoAction prevents that deletion fixup][ef-delete].
Validate the existing [database constraint][pg-fk] rather than replacing it merely
to rename its delete action. Neither layer may cascade deletion/unclassification.

Retain a minimal `ActivityTypeIdRedirects` table for removed duplicate IDs:
`LegacyId` integer primary key, `ActivityTypeId` integer NOT NULL FK to the survivor,
with NO ACTION, NOT DEFERRABLE and EF ClientNoAction. Both IDs are positive and
different. Populate one direct loser → survivor row per deleted duplicate in the
same cleanup transaction. No runtime merge, redirect chains, names or descriptions
belong in this map. Keep legacy IDs disjoint from live IDs and preserve/advance the
existing ID sequence beyond its pre-cleanup high-water mark; never reuse a removed
ID. This is the necessary numeric compatibility map, not a historical-label ledger.

Add one bounded import fact, in the import slice's own prerequisite migration:

| Existing owner | New fact/default | Purpose |
| --- | --- | --- |
| LocationImport | UnclassifiedActivityCount = 0, nonnegative integer | Accepted new Locations with invalid supplied Activity; count > 0 implies the Invalid warning. |

No account counter, creation counter, quota lock, warning-bit registry or persistent
historical Activity label is needed. Existing import counts start at zero because
past invalid inputs cannot be inferred. Neither this fact nor the redirect map
enters catalog responses, portable files or public/group DTOs.

### Cleanup and deployment transaction

1. Use the existing migration/release authority and backup procedure, with catalog
   writers/imports quiesced. Preflight UTF-8/PG18/function support, every legacy
   name, actual generated-key index capacity and live FK inventory referencing
   ActivityTypes. Currently Locations is the only such model/migration reference;
   the new redirect FK must also be accounted for. A 101-character name or valid
   script/emoji/format characters alone must not block deployment. Abort before
   product-data mutation for genuinely invalid legacy names, unaccounted references
   or actual key/description storage failure.
   Report IDs/reason codes without raw personal text. Repairing invalid legacy data
   is a deployment prerequisite, not an import-time administrator prompt.
2. In one migration transaction, lock the catalog and relevant referencing tables
   against writes in a fixed order. Temporary migration locks are acceptable;
   import-time table/global advisory locks are unnecessary. Compute display/key
   with v1 and a temporary loser-ID → lowest-ID survivor mapping.
3. Preserve the survivor's canonicalized display and combine descriptions by the
   policy below. Reassign **every** reference from losers to survivors, across all
   owners, updating only Activity FKs. Never rewrite UUIDs, LocationType, timestamps,
   notes, owner IDs, address/device data or reverse-geocoding provenance.
4. Persist every numeric redirect before deleting its loser; verify its survivor
   exists, mappings are direct, and no loser Location references remain. Delete
   losers, then verify legacy/live IDs are disjoint. Add the generated key,
   canonical-name check and unique index; validate the restrictive FK. Validate all
   before committing; failed validation rolls back the complete reconciliation.
5. Restart onto compatible Admin/API code with redirect resolution and authenticated
   catalog reads, before imports can create catalog entries. Empty-database seeding
   uses v1 and uniqueness; it remains initial reference data. Never reseed deleted/renamed
   entries merely because they differ from the bundled list. No live old/new
   writer overlap is supported during this schema transition.

EF migration history makes redeployment a no-op. The cleanup transformation also
does nothing when keys are already unique; it must not append description blocks
again. An interrupted transaction leaves the original data intact. A later Down
may remove constraints/key/function in dependency order but **cannot reconstruct
deleted IDs or original FK assignments**. It retains merged descriptions and
survivor associations. Exact pre-cleanup recovery requires the verified existing
backup with writers stopped; do not claim a lossless Down or create an audit table
solely to simulate one. Retain redirects and their resolver during code rollback;
an older API binary that cannot honor deleted IDs is not a compatible rollback.
Otherwise restore the verified pre-cleanup backup before restarting old writers.
Sequence advancement may leave harmless gaps after rollback; it must never allow
reuse of an ID retained for existing clients.

### Compatibility of cached and queued Mobile IDs

The inspected Mobile main is `8e28664faa33eec4c2fe79e3e266240ac06e9884`.
[ActivitySyncService][mobile-sync] sends GET `/api/activity` with the configured
Bearer token, parses the [existing array schema][mobile-schema] of integer `id`, string `name`, nullable
string `description`, and replaces positive-ID cache rows. Its normal refresh
interval is six hours. [QueuedLocation][mobile-queued-model] and
[PendingTimelineMutation][mobile-mutation-model] persist Activity IDs independently
of that cache; [queue drain][mobile-queue] and [timeline sync][mobile-updates] replay
them unchanged. Refreshing the cache does not rewrite pending requests or prove
that a device no longer holds an old ID.

For every existing backend path accepting ActivityTypeId, including
[POST check-in][location-api], [POST log-location][location-log] and [PUT location][location-update],
resolve a live ID first, otherwise its direct numeric redirect, before assigning
a new FK. Store the survivor ID. Preserve idempotency replay before new-write
validation, owner checks, response shapes, explicit clear/null behavior and current
unknown-ID/name fallback behavior. PUT's ActivityName lookup reuses v1's identity;
it does not create catalog entries. Neither numeric lookup nor ordinary API updates
acquire the import's automatic-creation authority. Negative Mobile fallback IDs
remain outside this duplicate-ID map; no new default-ID mapping is introduced.

Retain redirects without a time-based expiry: an offline queue has no established
maximum age. The FK protects each mapped survivor from deletion even when it has
no current Locations; Admin must report that compatibility reference. Rename keeps
the survivor ID and redirects valid. Retirement requires a separately approved
client-support decision with evidence; this design does not require or change a
Mobile build, cache schema, endpoint, refresh protocol or request payload.

### Description preservation

For each equivalent-name group, compare descriptions with exact ordinal equality,
without case folding or trimming content. Null/empty/whitespace-only descriptions
contain no relevant prose. Retain the survivor's nonblank description byte-for-byte
as the primary text, or use the lowest-ID nonblank description if it has none.
For each other distinct nonblank text, in ascending source ID, append two newlines,
a plain-text heading `Preserved description from Activity <ID> (<original Name>):`,
a newline and the original text. Keep the first/lowest-ID source for exact repeats.

This preserves contradictory descriptions without inventing a semantic winner or
requiring approval during execution. The source name in the heading explains the
merge; it is not an alias or lookup input. Keep Description as ordinary encoded
text in existing Razor views, never executable HTML. A trusted administrator may
later explicitly replace the whole Description through its existing edit form.
Imports carry Activity names only: do not import/export catalog descriptions or
accept extra file fields as authority to edit them. Thus incoming history cannot
overwrite or append to an administrator's description.

## 5. Import reconciliation, concurrency and safety

Use one concrete `ActivityCatalogReconciler` with the current DbContext/transaction;
the admin path reuses its normalization query, not its import creation capability.
No generic catalog framework or new endpoint is needed. Keep the parser's transient
ImportedActivityName; use a small nonpersisted invalid-value flag for wrong JSON
types/transport-invalid strings. Count and parse passes perform no catalog writes.
Preserve raw Activity text until this policy evaluates it rather than applying
different parser-specific trimming rules. Honor existing file/request resource
limits rather than inventing a catalog-name length limit.

For each existing 50-row batch:

1. Begin the existing PostgreSQL transaction and check execution authority. Retain
   the existing owner-row `FOR UPDATE` lock before inserts for a keyless batch.
   A keyed batch does not acquire that lock merely because it carries Activity.
2. Ignore source Activity IDs/navigation objects. Filter duplicate Locations first;
   insert candidates with null Activity FK through existing keyed savepoint
   arbitration. Identify the actual newly inserted rows, excluding detached race
   losers. These provisional rows are uncommitted and invisible externally.
3. Normalize only those new rows, in one bounded query; group by returned key and
   process keys in PostgreSQL `C` order. Within each key use earliest accepted file
   ordinal for display. Do not load the whole catalog or cache IDs between batches.
4. For each key, select an existing Activity **FOR SHARE**. This holds its name/ID
   stable against concurrent admin rename/delete until commit. If missing, always
   attempt parameterized `INSERT ... ON CONFLICT
   ("NormalizedName") DO NOTHING RETURNING "Id"`, then a **separate statement**
   selecting the winning row by key FOR SHARE under READ COMMITTED. A concurrent
   winner can be absent from the INSERT snapshot; a single CTE fallback is unsafe.
   [PostgreSQL documents this visibility boundary][pg-isolation].
5. Assign its FK only to the new rows for that key. Every newly created Activity
   must be linked to an actual new Location in the same committed transaction.
   No creation spending counter, no-op UPDATE of the winner or overwrite of
   existing catalog content. Existing keyed Location savepoints remain; catalog
   resolution needs no per-key savepoint for `ON CONFLICT DO NOTHING`.
6. Before commit, require every valid nonmissing Activity on an actual new Location
   to have its resolved FK. Save assigned FKs, the invalid-classification count,
   existing duplicate/enrichment facts and the checkpoint under the original
   epoch/xmin checks. Commit together.
   Cancellation, stale authority and rollback undo the entire uncommitted batch.

The [unique index conflict target][pg-insert] handles competing creators across
owners and administrator requests. If a winner vanishes/renames between statements,
retry resolution at most once; exhaustion requires whole-batch rollback/retry,
never null-FK success. Same-key imports converge; disjoint keys have no global
mutex. FOR SHARE intentionally blocks even a description edit briefly, making the
FK/name observation simple.
Use fresh contexts after a whole-batch rollback; reset destination IDs/FKs and
tracking state on that batch's new Location objects before retry, preserving their
raw parsed Activity values, invalid flags, UUIDs and other metadata. Rerun Location
arbitration and normalization, including concurrent UUID winners. Never retain
resolved Activity IDs across checkpoints or failed attempts.

The shared catalog is a deliberate limited side effect of **user-authorized**
history import. It is not administrator CRUD authority. Automatically create every
valid missing name with null Description. There is **no per-import or account
lifetime creation quota**, and no account spending counter or lock for one.

Use existing upload/admission, staged-file and owner-isolation boundaries. Bound
normalization/grouping to the existing 50-row batch and file/request byte ceiling;
SQL is parameterized, display output is encoded, and names/coordinates never enter logs or SSE. An
authorized user can introduce unwanted but syntactically valid categories. The
maintainer accepts shared-catalog growth under existing admission/resource bounds;
those bounds do not claim a lifetime catalog-size cap. All authenticated users can
read these shared names/descriptions; user import guidance must state this before
submission. No fuzzy filter, paid service or import approval mechanism is added.

### Failure and completion semantics

Use transaction-local catalog statement/lock timeouts, initially **5 seconds /
2 seconds**, with at most two successful-statement resolution attempts per key
for a vanished/renamed winner. On catalog lock/statement timeout, exhausted
winner resolution, deadlock or serialization failure, roll back the entire current
batch before retrying. Permit at most **two whole-batch retries**, with a bounded,
cancellation-aware backoff of at most 250 ms between attempts. All attempts and
backoff share one **30-second batch deadline**; each operation uses the remaining
budget. These settings bound work, not whether a valid classification may be lost.
Restore catalog-specific settings before unrelated work. Recheck exact execution
authority at each attempt and before committing. Cancellation/stale epoch stops
retry and rolls back; a batch deadline is failure, distinct from user cancellation.

When retries/deadline are exhausted, report failure through the existing import
lifecycle, retaining the prior checkpoint, committed prefix and staged file.
Existing owner restart/resume reparses from durable authority and resolves the
uncommitted tail once contention clears; it cannot overwrite the committed prefix.
No hidden queue, replacement scheduler or later classification backfill is needed.
Ordinary contention must yield either a correct FK or no committed batch advance.

Only input invalidity established before catalog mutation permits acceptance with
null FK and an Invalid warning. A catalog check failure for a supposedly valid
normalized name is a contract/schema failure, not evidence that the input is
invalid. Missing schema, permission/connection failures, unknown integrity errors
and actual storage/index capacity failures roll back and fail the batch. Never
convert these failures or user cancellation to Completed-with-warnings. Recover an
unprovable commit outcome from durable checkpoint/rows rather than guessing or
blindly retrying its side effects.

For accepted invalid input, finish as Completed with a visible warning summary,
for example “Locations imported; 3 Locations with unresolved Activity (invalid value).”
Status/detail views must show the durable count/reason on reload and restart, independent
of SSE. Never say all classifications completed when unresolved count is nonzero.
Do not replace terminal ErrorMessage or enrichment warnings. Only accepted new
Locations contribute; missing labels, duplicates and rolled-back work do not.

Option C needs **no separately persisted historical label**. A valid reconciled
Location exports its current destination Activity name. Rename changes that name
for its associated history by existing FK semantics. Invalid labels are not catalog
identities; durable warnings disclose their loss, and existing staged-file
retention preserves retry evidence until ordinary history deletion.
Replaying an already accepted missing/invalid classification cannot fill it;
subsequent correction uses ordinary owner editing. Valid resolution failures
never commit such a null row, so restart can safely finish their uncommitted tail.

## 6. Administrator CRUD and authenticated catalog contract

Retain existing Admin authorization, antiforgery, forms and alerts. Apply v1 before
write and translate exact named-constraint errors after concurrent arbitration.

| Action | Required response |
| --- | --- |
| Create invalid/empty name | Return existing form with Name validation error; preserve submitted fields. |
| Create an existing key, including a concurrent import winner | Name conflict with link to existing edit page; no second row or implicit description overwrite. |
| Rename to own key (case/surrounding whitespace/NFC change) | Update canonical display, retain ID, redirects and every Location association. Interior whitespace changes identity. |
| Rename to another row's key | Name conflict, original row unchanged; no automatic runtime merge or reference moves. |
| Rename to a free key | Keep ID/references; previous name is free for a later import. |
| Edit Description | Explicit replacement through existing form; not name identity or an import-merge action. |
| Delete entry with neither Locations nor numeric redirects | Delete normally; a future valid import may recreate that name with a new destination ID. |
| Delete referenced entry, including a racing new reference | Friendly “Activity is used by Locations” failure; neither deletion nor unclassification. |
| Delete a survivor targeted by numeric redirects | Friendly “Activity is retained for existing clients” failure; preserve target/redirects even if no Location references remain. |
| Concurrent delete/edit or unexpected persistence error | Existing not-found/failed-operation response with safe logging; never an unhandled database exception. |

Catch SQLSTATE 23505 only for `IX_ActivityTypes_NormalizedName`, and 23503 only for
the named Location/redirect reference constraints. An optional reference precheck
improves the message but never replaces the restrictive FKs. Actual name/index
capacity failure returns a safe Name validation error rather than an arbitrary
character limit or an unhandled exception. Do not bind generated keys/redirects
from forms or expose a new catalog administration system.

### Endpoint privacy and published-client compatibility

Require authentication for existing **GET `/api/activity`**, preserving route
matching for `/api/Activity`, method, Name ordering and the JSON array schema:
integer `id`, string `name`, nullable string `description`. Return current live
catalog rows only. No pagination, envelope, redirect metadata or generated keys
are added, and no new endpoint is introduced.

Use the established [GetUserFromTokenOrCookie][api-auth] mechanism before querying
the catalog. It honors the existing Identity cookie first and otherwise the
request-owned [IncomingApiTokenResolver][token-resolver] for Wayfarer's current
Bearer API tokens. Retain active-account checks, token matching/exclusions and
lookup-admission behavior. These are existing opaque API tokens, not a newly
introduced JWT bearer scheme. Cookie-only `[Authorize]` without the current token
adapter would break Mobile and does not satisfy this contract.

Requests without a valid active identity under that existing precedence, including
anonymous requests, return **HTTP 401 directly**, without catalog data, login HTML
or a 302 login redirect. Preserve
the existing admission filter's overload response rather than relabeling overload
as authentication failure. Do not change global cookie challenges or the existing
check-in token-only/update cookie-or-token and antiforgery behavior.

This closes anonymous disclosure; the catalog remains deliberately shared among
authenticated users. Imported personal labels and merged descriptions consequently
become visible to those users. No owner location/history data or credentials are
added to the response. Deploy this read gate and numeric-ID compatibility with
the catalog prerequisites, before automatic import creation is enabled.

## 7. Native history fidelity and compatibility

Add the smallest `.Include(l => l.ActivityType)` to the common authenticated owner
export query (or equivalent established projection), keeping its owner filter and
ordering. Seed, dispose context, and export in a fresh authenticated context in
tests. Export Activity **Name**, never its numeric ID or generated identity key.

| Native pair | Activity field retained | New optional LocationType field |
| --- | --- | --- |
| CSV / CsvLocationParser | `Activity` | `LocationType` column |
| Wayfarer GeoJSON / WayfarerGeoJsonParser | `properties.Activity` | `properties.LocationType` string/null |
| GPX / GpxLocationParser | `wf:activity` | `wf:locationType`, existing `https://wayfarer.app/schemas/gpx` namespace |
| KML / KmlLocationParser | ExtendedData `Activity` | ExtendedData `Data name="LocationType"` / `value` |

`LocationType` is an optional independent string, never normalized as Activity or
looked up in a catalog. Missing/null/zero-length values parse as null in all pairs;
all other string contents, including whitespace, retain their value. CSV null and
empty serialize as an ordinary empty field; empty/quoted-empty cells both read as
null. Do not introduce a null sentinel, escaping scheme or another schema layer.
XML writers must emit nonempty whitespace values for this field rather than reuse
helpers that omit every whitespace-only value; GPX's extension-presence check must
include LocationType so a field-only record also exports it.
The actual GPX/KML subtree readers must preserve whitespace-only field content;
their current `LoadOptions.None` behavior needs the smallest appropriate correction.
Exercise parse and re-export, not just writer output. Preserve significant interior
Activity whitespace too, including CR through XML character references where
needed; native round trips must not change the approved identity classes.

GeoJSON Activity must use raw string/null scalar validation, not `ToString()` of
numbers/objects. A malformed Activity value leaves the Location intact with an
invalid flag. LocationType wrong types yield null, without creating classifications
or rejecting otherwise valid history. Older files missing new fields remain valid;
unknown optional fields remain ignored. Keep format-specific existing field-name
compatibility, timestamp handling and standard XML/CSV escaping.

Preserve all existing UUID rules (CSV/Wayfarer GeoJSON carry stored nullable UUID;
GPX/KML remain on their existing keyless boundary), notes sanitization, receipt/event
times, address fields, reverse-geocoding tuple/provenance, sensors, device/app fields
and authenticated ownership. No source Activity FK or owner supplied in a file
grants authority. Third-party extension stripping remains outside native fidelity.
Trip KML, generic GeoJSON and public/group APIs remain separate contracts. Mobile
implementation is out of scope; backend catalog auth and numeric-ID compatibility
are prerequisites for this design's existing-client safety.

## 8. Implementation slices and proposed issue criteria

Do not implement or change issue bodies from this design PR. After independent
review and maintainer approval, propose three follow-ups and retain #814 as the
native fidelity slice. Use existing services, entities and test infrastructure.

| Slice | Proposed acceptance and dependencies |
| --- | --- |
| A — Catalog and existing API compatibility | Own v1, generated key/check/index, lowest-ID transactional cleanup, complete FK transfer, description preservation, numeric redirects/resolution on existing ID-consuming API paths, authenticated GET /api/activity and initial seeding. Qualify legacy/empty databases, rerun, recovery and unchanged mobile payloads. No import creation yet. |
| B — Admin consistency | Depend on A. Reuse v1; explicit create/rename conflict errors, description edits and safe Location/redirect-referenced deletion, including constraint races. Keep current Admin surface. |
| C — Import reconciliation | Depend on A/B. Own the invalid-count migration, transient invalid flag, post-arbitration reconciler, automatic shared-catalog creation without quotas, bounded whole-batch retry/rollback and failure summaries. Prove duplicate-only/late UUID-loser imports create nothing, valid contention cannot commit null classification, and replay preserves classifications. |
| D — #814 native fidelity | Depend on C for the complete export → destination-worker → re-export promise. Own fresh Activity loading and LocationType in all four pairs, legacy compatibility and metadata/owner regressions. No migration in D. |

A/B should normally share one implementation PR/deployment: cleanup, compatible
API reads/writes and Admin responses become usable together. No duplicate IDs may
be deleted while requests can reach a binary lacking numeric redirect resolution.
C's invalid-count migration stays with C; there is no account/creation-counter
migration. All schema/auth prerequisites land before dependent writers execute.
D's pure serializer/parser work can be prepared independently,
but do not claim destination reconciliation before C. Reinspect live main at each
slice; version/release sequencing belongs to the existing release authority.

**Proposed #814 amendment, only after approval:** link this contract and prerequisite
A/B/C issues; replace “resolves Activity by name where supported” with “valid names
reconcile under the approved Option C contract on a destination catalog”; retain
its no-migration/no-UUID-work exclusions. Acceptance requires a fresh authenticated
export, production parser and destination worker round trip for each native pair,
including existing/missing Activity and nullable LocationType. All other metadata,
owner isolation and same-key no-overwrite must survive. No schema work is silently
folded into #814, and no follow-up issue is created by the present assignment.

Update the existing user import/export and developer database guides in their
own implementation slices to describe automatic creation, resource bounds, invalid
classification warnings, duplicate cleanup/rollback and current-name exports.
Describe resource bounds without implying a creation quota, safe retry/resume for
valid contention, authenticated shared-catalog visibility and numeric-ID retention.
Add changelog entries when behavior ships, not for this unimplemented design.

## 9. Proportional acceptance and review gates

Use the [existing test project and relational fixtures][testing-guide]. These are
future acceptance requirements, not tests claimed as executed by this design PR.

| Seam | Smallest useful proof |
| --- | --- |
| Normalization | Table-driven section 2 vectors through the real database wrapper/function: mixed capitals, surrounding Unicode whitespace, distinct single/double-space/tab/NBSP interiors, XML-invalid/empty values, Greek NFC/sigma, script/emoji/format characters and preserved GPS/eBike. Ordinary 101-character and >1,024-byte storage-compatible names succeed. Assert key equality/inequality and database uniqueness; no second .NET matcher. |
| Migration PostgreSQL | Reuse [PostgresMigrationTestFixture][migration-fixture]. Seed pre-A case/surrounding-trim/Greek duplicates, distinct/null descriptions, cross-owner FKs and valid long/script names. Lowest ID survives; all Location metadata and distinct description bytes survive; interior-whitespace-distinct rows stay separate. Every loser has a direct numeric redirect, no ID reuse, and target deletion is constrained. Reapply is no-op; injected failure rolls back data/map; truly invalid names/unknown FKs/actual index capacity failure leave data unchanged. Qualify fresh schema and document lossy Down/compatible rollback. |
| CRUD | Extend [AdminActivityTypeControllerTests][admin-tests] for validation/response behavior. Focused PostgreSQL evidence owns normalized create/rename constraints and deletion with loaded Location/redirect dependents plus a controlled racing Location reference. Rename keeps numeric redirects valid; an unreferenced, unmapped entry still deletes normally. InMemory is not FK or uniqueness evidence. |
| Catalog HTTP authentication | Extend [ApiActivityControllerTests][activity-api-tests], reusing the routed TestServer/Identity-host conventions in [API boundary tests][auth-http-tests] and [IdentityRouteHost][identity-host]. Cookie-only and current Wayfarer Bearer-only requests each receive 200 with the same ordered array and exactly id/name/description. Anonymous, invalid-token and inactive-account requests receive 401, no login redirect/HTML or catalog data; verify the 401 before automatic redirect handling. Preserve cookie-first precedence and token-admission overload translation. No JWT adapter or global auth change. |
| Existing-client ID compatibility | After real duplicate cleanup, submit unchanged check-in, log-location and PUT payloads carrying a removed positive ID, as cached/queued Mobile does: existing success response, stored survivor FK, no classification loss. Cover a refreshed catalog still leaving an old queued ID, mapped survivor rename/delete protection, current live ID, explicit clear/null and existing unknown-ID behavior. Preserve token-only check-in, cookie/token PUT with current antiforgery, owner authorization and idempotent replay/no-overwrite. Use existing [API tests][location-api-tests] and relational fixtures; no Mobile edit/device matrix. |
| Import PostgreSQL | Reuse [bounded imports][bounded-tests] and [worker races][race-tests]. Two owners concurrently create the same missing key using barriers; one Activity and correct FKs result. One admitted file with 65 valid distinct missing names succeeds; seeded history representing over 256 prior creations cannot deny another valid creation. No account/creation spending counters. A controlled keyed race loser with a novel label creates nothing; duplicate-only replay cannot replace an owner edit. |
| Checkpoint/failure PostgreSQL | One 51-row case interrupts after the first checkpoint and resumes; creations/invalid counts agree with committed rows. Hold a competing creator or Admin edit beyond the configured lock timeout: either retry resolves the correct FK, or no current-batch Locations/creations/count/checkpoint advance; release contention and restart/resume safely. Invalid input alone accepts null FK with durable warning. Cancellation/stale epoch rolls back; committed replay neither overwrites classifications nor fills missing/invalid nulls. No quota-spending matrix. |
| Native fidelity | Extend [LocationExportRoundTripPostgresTests][roundtrip-tests] and existing [CSV][csv-parser], [GeoJSON][geojson-parser], [GPX][gpx-parser], [KML][kml-parser] tests. Four format cases use disposed seed/fresh export contexts, a destination with different Activity IDs and existing/missing keys, then actual worker/re-export. Assert Activity identity/name, LocationType, metadata and owner filtering. |
| Optional values | Parser/re-export cases for absent/null/empty and nonempty whitespace LocationType, including actual GPX/KML readers and LocationType-only extensions; preserve interior Activity spaces/tab/NBSP/CR. Cover invalid Activity strings/types and old files. Keep current CSV/GeoJSON UUID and explicit-offset regressions; no GPX/KML UUID work. |

Use real maintained PostgreSQL/PostGIS with the guarded connection and disposable
migration database. Deterministic barriers replace sleeps in concurrency cases.
Reuse existing controlled provider/enrichment seams. No new browser, crash harness,
exhaustive environment matrix or synthetic tests of harness internals is required.
Local relational qualification must actually run; a skipped guarded test in CI is
not PostgreSQL evidence. Exact-head CI and fresh independent review remain separate.

## 10. Readiness and remaining decisions

**CORRECTED DESIGN READY for fresh independent review.** The maintainer-approved
choices are applied throughout: valid missing names create automatically without
creation quotas; only surrounding whitespace is trimmed; mixed-capital Unicode
matching preserves internal spelling/display; catalog reads require existing
cookie or API-token authentication. Valid reconciliation failure rolls back the
current batch and preserves safe resume. Numeric redirects protect cached/offline
IDs without a Mobile contract change. No quota or broader equivalence decision is
delegated to an implementation agent.

Remaining risks are explicit: authenticated shared-catalog visibility and growth,
genuinely invalid legacy data or actual full-key index/storage capacity, PG-major
normalization drift, deployment/rollback coordination, and indefinite numeric-ID
retention for offline clients. Preflight must classify real legacy/storage failures
without arbitrary name-length/property blockers. A separate support decision is
needed only if redirects are later retired; retirement is not a prerequisite here.

This draft PR changes documentation only. Code, migration, concurrency, failure
and native fidelity qualification remain unimplemented. A fresh independent Codex
instance reviews the exact corrected PR head; the earlier CHANGES REQUIRED review
is not a PASS for this revision. Continue correction/review cycles until an
independent PASS, with maintainer approval still required before issue amendments
or implementation. Do not merge this PR, close #814, start follow-ups or claim
Option C is shipped from this document's readiness label.

[issue]: https://github.com/stef-k/Wayfarer/issues/814
[parent]: https://github.com/stef-k/Wayfarer/issues/805
[agents]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/AGENTS.md
[metadata-evidence]: https://github.com/stef-k/Wayfarer/issues/805#issuecomment-6084542747
[design-review]: https://github.com/stef-k/Wayfarer/pull/861#issuecomment-6131031378
[activity-model]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Models/ActivityType.cs#L3-L11
[initial-activity]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Migrations/20250428071912_InitialCreate.cs#L19-L31
[seed]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Models/ApplicationDbContextSeed.cs#L148-L475
[location-model]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Models/Location.cs#L8-L61
[import-metadata]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Models/Location.ImportMetadata.cs#L5-L14
[worker-resolution]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Services/LocationImportService.cs#L260-L275
[worker-batch]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Services/LocationImportService.cs#L202-L245
[deduplicator]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Services/LocationImportDeduplicator.cs#L9-L148
[export-query]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Areas/User/Controllers/LocationExportController.cs#L27-L151
[export-gpx]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Areas/User/Controllers/LocationExportController.cs#L167-L280
[export-kml]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Areas/User/Controllers/LocationExportController.cs#L292-L375
[activity-fk]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Migrations/20250428071912_InitialCreate.cs#L347-L354
[admin-crud]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Areas/Admin/Controllers/ActivityTypeController.cs#L57-L159
[csv-parser]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Parsers/CsvLocationParser.cs#L34-L141
[geojson-parser]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Parsers/WayfarerGeoJsonParser.cs#L85-L207
[gpx-parser]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Parsers/GpxLocationParser.cs#L74-L165
[kml-parser]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Parsers/KmlLocationParser.cs#L73-L155
[testing-guide]: testing.md
[migration-fixture]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/tests/Wayfarer.Tests/Infrastructure/PostgresMigrationTestFixture.cs
[admin-tests]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/tests/Wayfarer.Tests/Controllers/AdminActivityTypeControllerTests.cs
[bounded-tests]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/tests/Wayfarer.Tests/Services/LocationImportBoundedPostgresTests.cs
[race-tests]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/tests/Wayfarer.Tests/Services/LocationImportWorkerRacePostgresTests.cs
[roundtrip-tests]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/tests/Wayfarer.Tests/Controllers/LocationExportRoundTripPostgresTests.cs
[unicode-space]: https://www.unicode.org/Public/16.0.0/ucd/PropList.txt
[xml-characters]: https://www.w3.org/TR/xml/#charsets
[upload-policy]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Util/UploadRequestPolicy.cs#L3-L22
[activity-api]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Areas/Api/Controllers/ActivityController.cs#L7-L37
[api-auth]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Areas/Api/Controllers/BaseApiController.cs#L23-L73
[token-resolver]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Services/IncomingApiTokenResolver.cs#L7-L86
[location-api]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Areas/Api/Controllers/LocationController.cs#L56-L218
[location-log]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Areas/Api/Controllers/LocationController.cs#L384-L559
[location-update]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/Areas/Api/Controllers/LocationController.cs#L795-L880
[activity-api-tests]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/tests/Wayfarer.Tests/Controllers/ApiActivityControllerTests.cs
[location-api-tests]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/tests/Wayfarer.Tests/Controllers/ApiLocationControllerTests.cs
[auth-http-tests]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/tests/Wayfarer.Tests/Controllers/ApiTokenHttpBoundaryTests.cs
[identity-host]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/tests/Wayfarer.Tests/Infrastructure/IdentityRouteHost.cs
[mobile-sync]: https://github.com/stef-k/WayfarerMobile/blob/8e28664faa33eec4c2fe79e3e266240ac06e9884/src/WayfarerMobile/Services/ActivitySyncService.cs#L229-L291
[mobile-schema]: https://github.com/stef-k/WayfarerMobile/blob/8e28664faa33eec4c2fe79e3e266240ac06e9884/src/WayfarerMobile/Services/ActivitySyncService.cs#L25-L35
[mobile-queue]: https://github.com/stef-k/WayfarerMobile/blob/8e28664faa33eec4c2fe79e3e266240ac06e9884/src/WayfarerMobile/Services/QueueDrainService.cs#L719-L748
[mobile-updates]: https://github.com/stef-k/WayfarerMobile/blob/8e28664faa33eec4c2fe79e3e266240ac06e9884/src/WayfarerMobile/Services/TimelineSyncService.cs#L716-L729
[mobile-queued-model]: https://github.com/stef-k/WayfarerMobile/blob/8e28664faa33eec4c2fe79e3e266240ac06e9884/src/WayfarerMobile/Data/Entities/QueuedLocation.cs#L123-L129
[mobile-mutation-model]: https://github.com/stef-k/WayfarerMobile/blob/8e28664faa33eec4c2fe79e3e266240ac06e9884/src/WayfarerMobile/Data/Entities/PendingTimelineMutation.cs#L68-L77
[pg-strings]: https://www.postgresql.org/docs/18/functions-string.html
[pg-collation]: https://www.postgresql.org/docs/18/collation.html
[pg-generated]: https://www.postgresql.org/docs/18/ddl-generated-columns.html
[pg-isolation]: https://www.postgresql.org/docs/18/transaction-iso.html
[pg-insert]: https://www.postgresql.org/docs/18/sql-insert.html
[dotnet-ordinal]: https://learn.microsoft.com/en-us/dotnet/api/system.stringcomparer.ordinal?view=net-10.0
[ef-delete]: https://learn.microsoft.com/en-us/ef/core/saving/cascade-delete#optional-relationship-with-dependentschildren-loaded
[pg-fk]: https://www.postgresql.org/docs/18/ddl-constraints.html#DDL-CONSTRAINTS-FK
