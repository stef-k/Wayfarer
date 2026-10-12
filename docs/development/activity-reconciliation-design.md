---
title: Option C Activity reconciliation contract
---

# Option C Activity reconciliation contract

**Status: DESIGN READY for independent review and maintainer approval.**
This is a proposed implementation contract, not shipped behavior or authorization
to implement. It belongs to [#814][issue] under [#805][parent]. The maintainer's
Option C selection supersedes name-only lookup and historical-label-only designs.
The live #814 migration exclusion remains in force until explicitly amended.

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

1. Reject input exceeding **1,024 UTF-8 bytes**. Reject NUL and malformed Unicode
   before binding PostgreSQL text; .NET must use strict encoding, not replacement
   characters. This transport guard performs no name comparison or normalization.
2. Replace each Unicode 16.0 `White_Space` scalar with U+0020. The complete set is
   U+0009–000D, 0020, 0085, 00A0, 1680, 2000–200A, 2028–2029, 202F, 205F and 3000.
   Collapse consecutive U+0020 and trim it from both ends. Tabs and line breaks
   therefore become separators; they cannot remain in a display name.
3. Reject remaining C0/C1 controls, Unicode noncharacters, and Unicode 16.0
   `Default_Ignorable_Code_Point` characters, except U+200C/U+200D join controls
   and variation selectors U+FE00–FE0F/U+E0100–E01EF. Those exceptions preserve
   legitimate script/emoji spelling and remain significant. A name consisting
   entirely of these exceptions is invalid. Bidi overrides/isolates, zero-width
   space, BOM and soft hyphen are rejected, never silently removed.
4. Normalize the resulting display to **NFC**. Empty means missing. Otherwise
   require at most **100 Unicode scalars** and **400 UTF-8 bytes**. Preserve case,
   accents, punctuation, acronyms and intentional mixed capitalization.
5. Compute the key as `normalize(casefold(display COLLATE pg_catalog.pg_unicode_fast), NFC)`.
   Require at most **300 scalars / 1,200 UTF-8 bytes** for the key; overflow is
   invalid, never truncation. Return display or key according to the mode.

Embed the fixed whitespace/ignorable/noncharacter ranges in this one function;
do not infer them from the host locale, a mutable lookup table or a second .NET
Unicode implementation. The pinned [Unicode whitespace data][unicode-space] and
[ignorable data][unicode-ignorable] define those properties.

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
| `Eating  Out`, `Eating\tOut`, ` Eating Out ` (NBSP) | One key, `eating out`; display whitespace becomes ordinary single spaces. |
| `Πεζοπορία`, `ΠΕΖΟΠΟΡΊΑ`, equivalent decomposed tonos | One accented Greek identity; NFC display retains supplied case. |
| `ΟΣ`, `ος`, `οσ`; `Straße`, `STRASSE` | Respectively one identity through full Unicode folding, including final sigma and ß. |
| `GPS`, `eBike` | Preserve supplied display; do not title-case. |
| Walking versus Hiking; accented versus unaccented Greek; Latin A versus Greek Α | Different keys; no semantic, accent-removal or confusable matching. |
| I/i versus dotless ı; dotted İ versus i | Default Unicode folding, not Turkish locale rules; dotless and dotted variants remain distinct from plain i. |
| Null, empty, only accepted whitespace | Missing; no Activity. |
| NUL, bidi override, zero-width space, oversized value | Invalid; never create or truncate a name. |

Within a file, the first accepted occurrence supplies a missing entry's display.
Across concurrent transactions, the first committed creator establishes display;
later imports never rename it. This is explicit first-writer authority, not an
arrival-order-independent choice of capitalization or integer ID. All contenders
converge to the same persisted identity and FK. Administrators can change display.

## 3. Deterministic behavior matrix

Apply the rows below after Location duplicate arbitration. Description differences
never change name identity. A missing classification is different from an invalid
or denied classification, even though all three can have a null FK.

| Condition | Catalog/resulting new Location | Existing Locations and reported outcome |
| --- | --- | --- |
| Unique existing valid key | Reuse destination ID; retain destination display/description. | No existing row mutation. |
| Missing valid key, creation allowed | Create with canonical display and null Description; link atomically. | Charge only a committed creation. |
| Identity-equivalent existing duplicates | Deployment cleanup selects lowest ID and moves all references. | Classification meaning preserved; descriptions retained as below. |
| Unexpected duplicates after prerequisite deployment | Schema/configuration failure; rollback current batch and fail execution. | Keep prior prefix/staged file; never choose an arbitrary row. |
| Different normalized names | Keep separate entries, even with identical descriptions. | Walking/Hiking never merge. |
| Activity renamed Walking → Hiking; later import says Walking | Reuse a currently existing Walking or create a separate one. | Old Locations keep the renamed entry's FK. |
| Missing Activity | Null FK; no catalog work or warning. | Location is accepted unclassified. |
| Invalid Activity, including non-string JSON value | Null FK; count one unresolved classification. | Accept Location and metadata; show Invalid warning. |
| Creation quota reached | Existing keys still resolve; missing key gets null FK. | Accept Location; show Limit warning. |
| Recoverable value-specific creation failure | Roll back that key's savepoint; leave its new Locations unclassified. | Accept those Locations; show CreationFailed warning. |
| Duplicate Location, including late UUID race loser | No reconciliation/creation for discarded row. | Existing classification and all other metadata stay unchanged. |
| Retry of committed batch or already accepted unclassified Location | Skip existing Location using existing identity rules. | Do not fill or overwrite classification as a replay side effect. |
| Cancel/stale epoch or unrecoverable database failure | Roll back uncommitted Locations, creations, counters and checkpoint. | Retain committed prefix; use existing cancellation/failure lifecycle. |

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

Add only bounded import facts, in the import slice's own prerequisite migration:

| Existing owner | New fact/default | Purpose |
| --- | --- | --- |
| ApplicationUser | ImportedActivityCreationCount = 0, integer 0–256 | Account lifetime automatic-creation allowance; persists across deleted import history. |
| LocationImport | CreatedActivityCount = 0, integer 0–64 | Per-file allowance and truthful committed count across epochs/restarts. |
| LocationImport | UnclassifiedActivityCount = 0, nonnegative integer | Accepted Locations with a supplied but unresolved classification. |
| LocationImport | ActivityWarningFlags = 0, fixed allowed bits Invalid/Limit/CreationFailed | Bounded owner-visible reasons, independent of terminal ErrorMessage. |

No creation ownership table, merge ledger or persistent historical Activity label
is necessary. Counters prevent quota reset through retries/deleted import history;
warnings prevent silent classification loss. Existing data starts at zero because
its creator cannot be inferred. None of these facts enters public/group DTOs.

### Cleanup and deployment transaction

1. Use the existing migration/release authority and backup procedure, with catalog
   writers/imports quiesced. Preflight UTF-8/PG18/function support, every legacy
   name and the live FK inventory referencing ActivityTypes. Currently Locations
   is the only such model/migration reference. Abort before mutation for invalid
   legacy names, unaccounted references or a description result too large to store.
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
4. Verify no loser references remain, then delete losers. Add the generated key,
   canonical-name check and unique index; validate the restrictive FK. Validate all
   before committing; failed validation rolls back the complete reconciliation.
5. Restart onto compatible code. Empty-database seeding uses v1 and uniqueness;
   it remains initial reference data. Never reseed individual deleted/renamed
   entries merely because they differ from the bundled list. No live old/new
   writer overlap is supported during this schema transition.

EF migration history makes redeployment a no-op. The cleanup transformation also
does nothing when keys are already unique; it must not append description blocks
again. An interrupted transaction leaves the original data intact. A later Down
may remove constraints/key/function in dependency order but **cannot reconstruct
deleted IDs or original FK assignments**. It retains merged descriptions and
survivor associations. Exact pre-cleanup recovery requires the verified existing
backup with writers stopped; do not claim a lossless Down or create an audit table
solely to simulate one. Retain quota counters during a code rollback so re-upgrade
cannot replenish allowances; dropping them is a separate deliberate data rollback.

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
different parser-specific trimming rules. Discard oversized transient text early.

For each existing 50-row batch:

1. Begin the existing PostgreSQL transaction and check execution authority. Acquire
   the existing owner-row `FOR UPDATE` lock before Location inserts if the batch
   is keyless **or carries Activity values**. This also serializes same-owner quota
   spending without upgrading a parent lock after inserting child Locations.
2. Ignore source Activity IDs/navigation objects. Filter duplicate Locations first;
   insert candidates with null Activity FK through existing keyed savepoint
   arbitration. Identify the actual newly inserted rows, excluding detached race
   losers. These provisional rows are uncommitted and invisible externally.
3. Normalize only those new rows, in one bounded query; group by returned key and
   process keys in PostgreSQL `C` order. Within each key use earliest accepted file
   ordinal for display. Do not load the whole catalog or cache IDs between batches.
4. For each key, select an existing Activity **FOR SHARE**. This holds its name/ID
   stable against concurrent admin rename/delete until commit. If missing and
   both creation allowances permit it, use parameterized `INSERT ... ON CONFLICT
   ("NormalizedName") DO NOTHING RETURNING "Id"`, then a **separate statement**
   selecting the winning row by key FOR SHARE under READ COMMITTED. A concurrent
   winner can be absent from the INSERT snapshot; a single CTE fallback is unsafe.
   [PostgreSQL documents this visibility boundary][pg-isolation].
5. Use a per-key savepoint around creation/resolution. Charge counters only for a
   newly created row that remains linked to at least one actual new Location in
   this committed transaction. Assign its FK only to the new rows for that key.
   No no-op UPDATE of the winner and no overwrite of existing catalog content.
6. Save assigned FKs, unresolved counts/flags, existing duplicate/enrichment facts
   and the checkpoint under the original epoch/xmin checks. Commit together.
   Cancellation, stale authority and rollback undo the entire uncommitted batch.

The [unique index conflict target][pg-insert] handles competing creators across
owners and administrator requests. If a winner vanishes/renames between statements,
retry resolution at most once, then treat the key as CreationFailed. Same-key
imports converge; disjoint keys have no global mutex. FOR SHARE intentionally
blocks even a description edit briefly, making the FK/name observation simple.
Use fresh contexts after a whole-batch rollback; reset destination IDs/FKs and
tracking state on that batch's new Location objects before retry, preserving their
parsed UUIDs/metadata. Never retain resolved Activity IDs across checkpoints.

The shared catalog is a deliberate limited side effect of **user-authorized**
history import. It is not administrator CRUD authority. Allow only validated name
creation with null Description, at most **64 actual creations per durable import**
and **256 per account lifetime**. Exhaustion still permits reuse of existing keys.
Deleting an Activity or import does not refund lifetime allowance. Check allowances
again under the owner lock and persist increments with the Location checkpoint.
These prospective constants bound one account's catalog growth across repeated
files; total growth is bounded by authorized account population plus trusted admin
creation. They are not a defense against unrestricted creation of new accounts.

Use existing upload/admission, staged-file and owner-isolation boundaries. Bound
normalization/grouping to 50 rows and 1,024 bytes per name; SQL is parameterized,
display output is encoded, and names/coordinates never enter logs or SSE. An
authorized user can introduce unwanted but syntactically valid categories within
the allowance; that consequence is accepted by Option C, not obscured as an admin
operation. No fuzzy filters, paid service, alias storage or import approval exists.

### Failure and completion semantics

Set transaction-local SQL statement/lock timeouts to **5 seconds / 2 seconds** for
catalog operations, with at most two resolution attempts per key. Limit a batch
transaction to **30 seconds** and at most one whole-batch retry for deadlock or
serialization failure; retain cancellation and exact execution checks on retry.
Restore catalog-specific settings before unrelated work. A batch deadline rolls
back and fails execution without retry; distinguish this deadline from user
cancellation. No timeout starts a hidden queue. A query failure outside a per-key
savepoint is a batch failure, never evidence that all values are invalid.

Only a known per-value canonical-name check failure, catalog lock timeout, or
exhausted winner-resolution race is recoverable locally: roll back its savepoint,
clear that key's provisional assignments/increments, and accept its Locations
with null FK and CreationFailed. Invalid input and quota denial need no failing SQL.
User cancellation is never converted to a warning. Missing function/index/schema,
permission failure, connection loss, unknown integrity failures, deadlock or
serialization exhaustion fail the current batch using the existing lifecycle.
An unprovable commit outcome is recovered from durable checkpoint/rows, not guessed.

On local fallback, finish as Completed with a visible warning summary, for example
“Locations imported; 3 supplied Activities could not be reconciled (creation limit).”
Status/detail views must show durable count/flags on reload and restart, independent
of SSE. Never say all classifications completed when unresolved count is nonzero.
Do not replace terminal ErrorMessage or enrichment warnings. Only accepted new
Locations contribute; missing labels, duplicates and rolled-back work do not.

Option C needs **no separately persisted historical label**. A valid reconciled
Location exports its current destination Activity name. Rename changes that name
for its associated history by existing FK semantics. Invalid/denied/failed labels
are not catalog identities; durable warnings disclose their loss, and existing
staged-file retention preserves retry evidence until ordinary history deletion.
Replaying an already accepted null classification cannot fill it; subsequent
correction uses ordinary owner editing. This is an explicit compatibility limit,
not an indefinitely pending classification or an administrator execution gate.

## 6. Administrator CRUD contract

Retain existing Admin authorization, antiforgery, forms and alerts. Apply v1 before
write and translate exact named-constraint errors after concurrent arbitration.

| Action | Required response |
| --- | --- |
| Create invalid/empty name | Return existing form with Name validation error; preserve submitted fields. |
| Create an existing key, including a concurrent import winner | Name conflict with link to existing edit page; no second row or implicit description overwrite. |
| Rename to own key (case/spacing/display change) | Update canonical display, retain ID and every Location association. |
| Rename to another row's key | Name conflict, original row unchanged; no automatic runtime merge or reference moves. |
| Rename to a free key | Keep ID/references; previous name is free for a later import. |
| Edit Description | Explicit replacement through existing form; not name identity or an import-merge action. |
| Delete unreferenced entry | Delete normally; a future valid import may recreate that name with a new destination ID. |
| Delete referenced entry, including a racing new reference | Friendly “Activity is used by Locations” failure; neither deletion nor unclassification. |
| Concurrent delete/edit or unexpected persistence error | Existing not-found/failed-operation response with safe logging; never an unhandled database exception. |

Catch SQLSTATE 23505 only for `IX_ActivityTypes_NormalizedName`, and 23503 only for
the Activity-reference constraint. An optional reference precheck improves the
message but never replaces the restrictive FK. Do not bind generated keys/counters
from forms or expose a new catalog administration system.

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
Trip KML, generic GeoJSON, public/group APIs and mobile work are separate contracts.

## 8. Implementation slices and proposed issue criteria

Do not implement or change issue bodies from this design PR. After independent
review and maintainer approval, propose three follow-ups and retain #814 as the
native fidelity slice. Use existing services, entities and test infrastructure.

| Slice | Proposed acceptance and dependencies |
| --- | --- |
| A — Catalog invariant | Own v1, generated key/check/index, lowest-ID transactional cleanup, complete FK transfer, description preservation, restrictive deletion mapping and initial seeding. Qualify legacy/empty databases, rerun and recovery. No import creation yet. |
| B — Admin consistency | Depend on A. Reuse v1; explicit create/rename conflict errors, description edits and safe referenced deletion, including constraint races. Keep current Admin surface. |
| C — Import reconciliation | Depend on A/B. Own small quota/warning-counter migration, transient invalid flag, post-arbitration reconciler, bounded shared-catalog creation and failure summaries. Prove duplicate-only and late UUID-loser imports create nothing, and replay preserves classifications. |
| D — #814 native fidelity | Depend on C for the complete export → destination-worker → re-export promise. Own fresh Activity loading and LocationType in all four pairs, legacy compatibility and metadata/owner regressions. No migration in D. |

A/B can share one implementation PR if that is simpler: the invariant becomes
usable without shipping an Admin path that leaks new unique-constraint exceptions.
C's counter migration stays with C; all schema prerequisites land before dependent
writers execute. D's pure serializer/parser work can be prepared independently,
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
own implementation slices to describe automatic creation, limits, incomplete
classification warnings, duplicate cleanup/rollback and current-name exports.
Add changelog entries when behavior ships, not for this unimplemented design.

## 9. Proportional acceptance and review gates

Use the [existing test project and relational fixtures][testing-guide]. These are
future acceptance requirements, not tests claimed as executed by this design PR.

| Seam | Smallest useful proof |
| --- | --- |
| Normalization | Table-driven vectors from section 2 through the real database wrapper/function; include 100/101 scalars, 1,024/1,025 raw bytes, control/empty values, Greek NFC/sigma and preserved GPS/eBike. Assert identical returned keys and database uniqueness; no separately reimplemented .NET matcher. |
| Migration PostgreSQL | Reuse [PostgresMigrationTestFixture][migration-fixture]. Seed pre-A duplicates with case/spacing/Greek equivalents, distinct/null descriptions and cross-owner FK references. Run the actual migration; lowest ID survives, all references/metadata and distinct description bytes survive, differently named rows stay separate. Reapply is no-op; injected failure rolls back; invalid names/unknown FK preflight fails unchanged. Qualify fresh schema and document lossy Down. |
| CRUD | Extend [AdminActivityTypeControllerTests][admin-tests] for validation/response behavior. Focused PostgreSQL evidence owns normalized create/rename constraints and referenced deletion with loaded dependents plus a controlled racing reference. InMemory is not FK or uniqueness evidence. |
| Import PostgreSQL | Reuse [bounded imports][bounded-tests] and [worker races][race-tests]. Two owners concurrently create the same missing key using barriers; one Activity and correct FKs result. Same-owner near-limit imports cannot overspend. A controlled keyed race loser with a novel label creates no entry. Duplicate-only replay creates nothing and cannot replace an owner edit. |
| Checkpoint/failure PostgreSQL | One 51-row case interrupts after the first checkpoint and resumes; creations/counters agree with committed rows. A value-specific creation failure preserves new Locations/null FK and durable warnings. Cancellation/stale epoch rolls back current batch; committed replay neither spends again nor fills prior nulls. Cover per-file/account limit boundaries without hundreds of fixtures. |
| Native fidelity | Extend [LocationExportRoundTripPostgresTests][roundtrip-tests] and existing [CSV][csv-parser], [GeoJSON][geojson-parser], [GPX][gpx-parser], [KML][kml-parser] tests. Four format cases use disposed seed/fresh export contexts, a destination with different Activity IDs and existing/missing keys, then actual worker/re-export. Assert Activity identity/name, LocationType, metadata and owner filtering. |
| Optional values | Parser cases for absent/null/empty and nonempty whitespace LocationType, invalid Activity strings/types, and old files. Keep current CSV/GeoJSON UUID and explicit-offset regressions; do not add GPX/KML UUID work. |

Use real maintained PostgreSQL/PostGIS with the guarded connection and disposable
migration database. Deterministic barriers replace sleeps in concurrency cases.
Reuse existing controlled provider/enrichment seams. No new browser, crash harness,
exhaustive environment matrix or synthetic tests of harness internals is required.
Local relational qualification must actually run; a skipped guarded test in CI is
not PostgreSQL evidence. Exact-head CI and fresh independent review remain separate.

## 10. Readiness and remaining decisions

**DESIGN READY.** This contract selects normalization, survivor/display/description
authority, safety allowances, failure behavior and issue split; no product choice
is left unspecified. Quantitative limits are proposed fixed defaults for approval,
not an invitation for an implementation agent to invent a different trust boundary.
Invalid existing catalogs can require deployment remediation, which is a stated
operational prerequisite rather than an import-time approval mechanism.

This draft PR changes documentation only. Code, migration, concurrency, failure
and native fidelity qualification remain unimplemented. A fresh independent Codex
instance reviews the exact PR head; maintainer approval is required before issue
amendments or implementation. Do not merge this PR, close #814, start follow-ups or
claim Option C is shipped from this document's readiness label.

[issue]: https://github.com/stef-k/Wayfarer/issues/814
[parent]: https://github.com/stef-k/Wayfarer/issues/805
[agents]: https://github.com/stef-k/Wayfarer/blob/9d8ee6b4132dfef53beb167b86b282be31c88973/AGENTS.md
[metadata-evidence]: https://github.com/stef-k/Wayfarer/issues/805#issuecomment-6084542747
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
[unicode-ignorable]: https://www.unicode.org/Public/16.0.0/ucd/DerivedCoreProperties.txt
[pg-strings]: https://www.postgresql.org/docs/18/functions-string.html
[pg-collation]: https://www.postgresql.org/docs/18/collation.html
[pg-generated]: https://www.postgresql.org/docs/18/ddl-generated-columns.html
[pg-isolation]: https://www.postgresql.org/docs/18/transaction-iso.html
[pg-insert]: https://www.postgresql.org/docs/18/sql-insert.html
[dotnet-ordinal]: https://learn.microsoft.com/en-us/dotnet/api/system.stringcomparer.ordinal?view=net-10.0
[ef-delete]: https://learn.microsoft.com/en-us/ef/core/saving/cascade-delete#optional-relationship-with-dependentschildren-loaded
[pg-fk]: https://www.postgresql.org/docs/18/ddl-constraints.html#DDL-CONSTRAINTS-FK
