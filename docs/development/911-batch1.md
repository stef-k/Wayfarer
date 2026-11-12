# #911 Batch 1: portable fields and reuse policy

Scope: Batch 1 only. No comparison workspace, matching, composition initiation,
Finish/Delete, selective placement or Copy-to-existing is implemented by this slice.
The accepted issue body remains the product contract. Base:
`d9351a37f30a208ced492c5d6ed1a36409b17342`; decoded body SHA-256:
`3393c7f4ba24b2617b4e0a9328ea61c86ce725b2ecfef189c316331c7aecaeb2`.
The final design review is [the whole-contract adversarial review](https://github.com/stef-k/Wayfarer/issues/911#issuecomment-6803676313).

## Portable contract and examples

`Segment.title` is nullable, at most 200 characters, independent of mode, Notes,
route and planning. An empty authored title clears it; omission keeps it. Historical
rows and supported older files use the existing derived journey label.

`Trip.contributionPolicy` is nullable legacy absence, or exactly `CC-BY-4.0`,
`CC0-1.0`, `view-only`. It governs the owner's contributions. It neither substitutes
for inherited rights nor proves permission for provider media. New Trips stay
private. First publication visibly selects CC BY before confirmation. Existing
explicit choices are retained. Legacy public Trips remain viewable, effectively
view-only for other-user reuse. No migration manufactures a grant.

`Trip.credits` is an ordered list of at most 32 self-contained records. Contributor
or source description must be readable. Contributor/source title: 200 characters;
licence/terms and notices: 4000 each; modifications: 2000; optional absolute HTTP(S)
URLs without credentials: 2000. Unknown readable inherited licence text survives;
it never becomes another selectable author licence. Complete JSON is bounded at
262144 characters and depth 16. Duplicate/unknown typed properties refuse.

```json
{
  "contributionPolicy": "CC0-1.0",
  "credits": [{
    "contributor": "Bob", "sourceTitle": "Taiwan ideas",
    "licence": "CC-BY-4.0",
    "licenceUrl": "https://creativecommons.org/licenses/by/4.0/",
    "notices": "Supplied source notice",
    "modifications": "Selected places adapted", "sourceUrl": null
  }]
}
```

Cookie `PATCH /api/trips/{tripId}/editor/metadata/conditional` and bearer
`PATCH /api/trips/{tripId}/conditional` accept the same independently protected
`contributionPolicy` and `credits` fields alongside their existing fields. Submit
only selected changes and their relevant originals. Credits clear is `[]`; nullable
policy clear is `null`, yielding effective view-only. Publishing a previously
unlicensed Trip requires an explicit policy in the same atomic request.

```json
{
  "expected": { "contributionPolicy": null, "credits": [] },
  "changes": {
    "contributionPolicy": "CC0-1.0",
    "credits": [{ "contributor": "Bob", "sourceTitle": "Offline source",
      "licence": "CC-BY-4.0", "licenceUrl": null, "notices": "Retain notice",
      "modifications": "Adapted", "sourceUrl": null }]
  }
}
```

The cookie/bearer Segment conditional endpoint accepts title with Notes, or alone:

```json
{ "expected": { "title": null }, "changes": { "title": "Morning walk" } }
```

```json
{ "expected": { "title": "Morning walk" }, "changes": { "title": null } }
```

Cookie path: `/api/trips/{tripId}/editor/segments/{segmentId}/conditional`.
Bearer path: `/api/trips/{tripId}/segments/{segmentId}/conditional`.
The existing strict `segment-route-notes-1` route envelope accepts optional
`expected.title`/`changes.title` together with route and independently authored
Notes. Legacy complete drafts retain xmin protection; omitted title never resets
it. Current selected originals are compared after waits; stale combined intent
refuses atomically. Ordinary errors, immutable attempts and newer drafts persist.

Public `/api/trips/{id}` remains planning-redacted, including for owners. The
separate coherent owner `/api/trips/{id}/planning` retains private/no-store
semantics. Complete graph DTO additions are `title`, `contributionPolicy`, `credits`,
Segment `estimatedDurationTicks`, `estimatedDurationSource`, `routeMappingMode`, and
Place `addressEnrichmentProvider`, `addressEnrichmentStorageMode`, `addressEnrichedAt`.
Existing duration minutes/distance kilometres remain unchanged. Null is distinct
from zero. Historical provider profile/configuration identities are scalar
provenance, never mutation authority. No credentials or private origins are added.

## Native v5, Google and media

Every new native file has Document-level `WayfarerSchemaVersion=5` inside standard
KML 2.2, even empty/default/personal-unchecked output. Document `ContentReuse`
is the nullable policy encoded as an empty value or exact policy string; `Credits`
is the shared typed JSON list. Every Segment has `Title`, empty for explicit absence.
These fields require v5. Versionless/older supported files remain readable; their
omission preserves root credits/policy and same-type retained Segment titles on
reviewed replacement. Independent import/copy gives fresh identities and planning
entry keys. Imported source grants become readable inherited credits; the new
owner's publication choice is unset. Imports remain private.

```xml
<kml xmlns="http://www.opengis.net/kml/2.2">
  <Document>
    <name>Empty portable Trip</name>
    <ExtendedData>
      <Data name="WayfarerSchemaVersion"><value>5</value></Data>
      <Data name="TripId"><value>cc7611bd-7fe5-4433-bd46-5a933572de47</value></Data>
      <Data name="ContentReuse"><value></value></Data>
      <Data name="Credits"><value>[]</value></Data>
    </ExtendedData>
  </Document>
</kml>
```

This minimal valid input demonstrates empty/historical content without invented
coordinates. Generated files also include the established root metadata. Planning
remains independently default-off; explicit active-owner personal opt-in preserves
the full ordered aggregate on Trip/Region/Place/Area/Segment. Native files retain
eligible Region covers, all polygon holes, exact measurements and credential-free
route/enrichment provenance. Native import never uses generic route simplification.
Supported older explicit-version readers refuse v5; upgrade before reading it.

Replacement preview includes portable rows alongside planning, using the existing
current-content-bound review permits. Default Keep preserves retained terms/titles;
Use/Clear is explicit. Removing/reidentifying a titled object requires loss consent.
A stale file/current-content binding cannot authorize a changed graph. Root fields
are reapplied after tracker clearing, together with reconstructed children.

Google remains generic standard KML, with no native version/graph IDs added for
attribution. One Document `Data name="WayfarerAttribution"` contains:

```json
{ "version": 1, "sourceTitle": "Taiwan ideas", "contributionPolicy": "CC0-1.0",
  "credits": [{ "contributor": "Bob", "sourceTitle": "Offline source",
    "licence": "CC-BY-4.0", "licenceUrl": null, "notices": "Retain notice",
    "modifications": "Adapted", "sourceUrl": null }] }
```

Its Document description contains the shared codec's complete readable projection.
Recognized typed/readable disagreement refuses. Generic CreateNew preserves terms
before persistence; generic Upsert remains unsupported. Malformed, duplicate,
misplaced, downgraded or unsupported reserved claims anywhere in XML refuse before
legacy/generic fallback. External Google ingestion/re-export remains best effort,
separate from native fidelity. PDF contains actual readable credits.

Clone and all native/Google/PDF acquisition, generation, progress and final delivery
check current reuse policy and active authority. Delivery is private/no-store, so
an earlier public grant is not an authorization cache. Public online viewing stays
available. Owner exports retain access. A policy change while acquisition waits
serializes before capture or aborts. No manual-copy prevention is promised.

Foreign covers and embedded Notes images have no transferable permission evidence
in the current reference model. Clone explicitly reports their safe omission and
asks authors to select permitted images; foreign exports omit those references.
No automatic fetching/rehosting establishes permission, and #493 remains separate.
Authorized owner references remain available. Credits themselves are plain text;
Razor/DOM/XML renderers escape values and the codec validates optional links.

## Mobile coordination and field matrix

No mobile source or runtime/native qualification is included. Implement complete
transport/storage mappings before claiming compatibility; Foundation's display
Trip and session TripOwner are not download schemas. No enablement flag is added.

| Field boundary | Required transport/storage treatment |
| --- | --- |
| Identity/parents | Preserve server/account/Trip scope and UUIDs on reads; remap independent copies and all references only under backend rules. |
| Empty/historical | Permit empty graphs, nullable location/center/orders/endpoints; invent no Place or geometry. |
| Order | Preserve per-parent sequences and nullable Place/Area order values; no display-role/date sort replacing saved order. |
| Geometry | Point `[lon,lat]`; Area `geometryGeoJson` and Segment `routeJson` are JSON strings. Preserve shells/holes, axes and precision. |
| Route state | `hasCustomRoute`, nullable geometry, From/To, loops, alternatives, ordered Via position and vertex indices stay distinct. |
| Measurement | Preserve nullable distance double, exact duration ticks, minutes and Automatic/Manual source independently. |
| Provenance | Current profile and historical profile/configuration/mapping/generated-at/instructions/attribution/storage remain distinct. |
| Enrichment | Preserve the coherent address/feature/provider/storage/time tuple; never invent provider evidence. |
| Metadata/media | Rich Notes, covers, styles, tags and viewport retain their authorized complete representations. |
| Portable fields | Dedicated title and full typed policy/credits, safe readable presentation, missing versus explicit Clear. |
| Planning | Full ordered format-1 aggregate on all five target kinds, civil precision, zones/folds, repeats, labels/kind/allocation and nullable absence. |
| Privacy | Public redaction does not establish absence of owner planning. Keep account/audience caches separate; respect private/no-store. |
| Protected writes | Submit selected fields with exact relevant originals; retain drafts after stale refusal or uncertain communication. |
| Native transport | Full v5/older codecs and durable graph remain mobile work. Cookie/antiforgery upload/review is not a bearer upload API. |
| Later batches | Workspace/resume, matching, placement, composition Finish/Delete and existing-Trip copy remain unimplemented. |

## Migration and evidence map

`AddTripPortableFieldsPolicy` adds nullable bounded Segment Title and Trip policy,
and nonnullable JSONB credits defaulting to `[]`. No old row receives a licence.
Apply through normal EF migration. Down drops these three columns and loses their
content; retain a backup/native-v5 export and restore compatible binaries before
rollback. Older binaries cannot preserve v5 data and must refuse v5 input.

| Requirement | Stable evidence |
| --- | --- |
| Defaults/legacy/owner/mixed policy; actual service reloads | `PortableFieldsPolicyPostgresTests`, existing Clone/export controller/service tests |
| Protected omissions/stale/disjoint/atomic fields | Existing conditional PostgreSQL suites plus portable mutation case |
| Newer title/credits and delayed acknowledgements | `segmentConditionalNotes`, `tripSharingIntent`, `tripEditorExit` client selections |
| Native complete pipeline, older retention/removal/refusal | Native/Polygon KML, planning import and portable PostgreSQL selections |
| Google typed/readable preservation/refusal | Shared codec/parser tests and actual generic import/re-export cases |
| Public privacy/freshness and personal opt-in | Complete read/DTO, planning, measurement, timestamp and native selections |
| Author/public controls/screenshots | One bounded managed browser smoke; denial/race matrices remain below browser |
| Migration/model | Guarded existing PostgreSQL migration plus EF pending-model check |

Validation receipts and any limitations are recorded in the draft PR and #911
progress comment. Source/design conclusions are distinct from fresh runtime results;
mobile/native application qualification and independent complete-PR review remain
separately owned gates.


## Implementation validation receipt

Implementation source checkpoint: `6abf3685cdec6141db45315f6fec15a63147445a`.
Final documentation/test-only delta is identified in the PR; it does not change
runtime source. Local source qualification used stable .NET 10 and Node 24.22.2.

- Complete affected .NET selection: 479 passed, no skips. The additional coherent
  API snapshot/DTO/address/antiforgery/cryptography selection: 122 passed, no skips;
  it overlaps the first selection.
- Complete client selection: 560 passed, no skips. Typecheck, Vite build, built
  assets, Release build/publish, published-file asset graph and EF model consistency
  checks passed. No full-repository .NET green claim is made.
- One bounded managed-browser selection passed with both screenshots and verified
  fixture/environment cleanup. Its initial run used an invalid Automatic-duration
  draft without a profile; the allowed correction exposed a real public-row title
  omission. The rendering regression fixed that product defect, permitting the
  final bounded run under the repository's explicit exception. No further browser
  campaign ran. Subsequent export-contention translation and a TypeScript return
  annotation are outside the smoke's author/public presentation scope.
- The acquisition-wait test proves revocation cannot produce a file; PostgreSQL's
  serialization refusal is translated to normal conflict. A consolidation attempt
  invalidated Debug static assets while Release bundling ran concurrently; the
  full selection passed after sequential Debug regeneration. Earlier Node 26
  trust failures were environment evidence; final Node 24 selections passed.
- Code Guard complete branch scope: REVIEW only, no policy changes. Existing
  large controllers/views, cohesive draft closures, native classifiers, export
  orchestration and grouped regression fixtures retain their responsibility
  boundaries. Large API/changelog documents remain navigable reference/history;
  this focused brief carries the new detail. Reviewed size/complexity/nesting
  findings do not justify mechanical splitting or policy relaxation.

At the implementation checkpoint, independent complete-PR review, current-head hosted CI,
maintainer acceptance and mobile/native application qualification were unperformed.
The correction receipt below supersedes that checkpoint status. Batch 1 does not
complete #911. No later-batch implementation, merge, closure or deployment occurred.


## Consolidated correction receipt

Batch 1 remains implemented but unaccepted. The [independent review](https://github.com/stef-k/Wayfarer/pull/975#pullrequestreview-5878017067)
reviewed `ed97812afee769c72d1b488146f00394ef0ed260`; corrections retain the pinned
base and accepted issue-body hash above. No contract edit or later batch is included.

| Finding | Correction and decisive evidence |
| --- | --- |
| Accepted null/omitted Contributor crashes Clone/CreateNew | Preserve the JSONB-loaded inherited records; construct the appended contribution separately. Six persisted copy/import cases include explicit null, omitted contributor and empty-string title/licence coincidence with `Earlier source notice`. |
| Inherited credit reassigned to current owner | Only the newly appended contribution gets `New owner`; the earlier record retains exact field values. No owner-name projection over inherited credits. |
| Blank display name hides public terms | Both compiled Viewer presentations render credits outside optional bylines; blank and named controls require two notice presentations. |
| Faint inherited-terms help | Theme-muted help colour replaces inherited error colour; existing author/public smoke owns the visual observation. |
| Segment hover ignores authored title | Live saved/draft/work presentation carries title and escapes it in the hit tooltip; derived role/direction fallback remains. Removed the obsolete unused adapter renderer rather than mistaking it for the live path. Existing Leaflet component test exercises the real resolver/layer. |
| Unintended tag/Area policy fields | Removed from ApiTagDto and WayfarerKmlArea; Trip fields remain, with exact tag and Place inventories. |
| Canonical Region: 7 failures | Grant only the successful/structural Clone fixtures so canonical identity and duplicate rejection execute. |
| API Clone: 2 failures | Explicit BY on the two intended foreign-copy fixtures. |
| Web Clone: 8 failures | Lawful success fixtures; foreign covers/images omitted and warmup never called. Removed the obsolete foreign-media warmup-failure test: its required fetch is forbidden, and omission/no-fetch is now the reachable contract. |
| Rich Notes: 2 failures | Owner exports private imports; object descriptions remain sanitized rich text, while Document attribution is asserted separately. |
| Measurement export: 9 failures | Explicit owner context for private export/re-export, real routed context and licensed anonymous positive case. Historical v3 removes later protected claims and is classified as v3 before injecting invalid measurements/provenance. |
| Measurement authority: 2 failures | Explicit grant reaches the accepted-provider Clone checks. |
| Coordinates: 1 failure | Exact Place inventory includes the three enrichment fields. |
| Active account: 1 failure | Inactive owner denied even when public/licensed; reactivated eligible source succeeds anonymously. |
| PDF orchestration: 4 failures | Licensed fixtures reach the mocked browser factory, retaining cancellation/budget/content assertions. |
| Application image qualification | Explicit BY seed, view-only native/Google/PDF/SSE refusal, then restored grant for positive SSE/PDF. Docker execution remains distinct from tooling checks. |
| ARM64 rustup prerequisite | Exactly 1.29.3 added; 1.29.4/1.30.0 still refused. Compiler 1.90.0, rustls/no-curl, distribution authority, override rejection and host trust controls unchanged. Primary-source assessment is in security.md. |

The original review logs remain under
`~/.local/share/wayfarer-evidence/911-batch1-ed97812a/independent-review/`.
Fresh correction logs, TRX, source-assessment material and the independent-review
handoff are retained under `~/.local/share/wayfarer-evidence/911-batch1-corrections/`.
The draft PR report binds the exact final SHA and final counts, including fresh
versus reused evidence and environment limitations. An earlier failing receipt is
never overwritten by a passing rerun.

The review's complete Batch-1 matrix remains applicable: protected title/Notes/route
and metadata intent; private/public coherent reads and freshness; current authority
through acquisition/waits/delivery; safe media omission; independent graph/identity,
ordering, geometry, measurement/provenance and planning fidelity; complete native v5,
older omission/replacement and reserved-claim refusal; generic Google attribution;
PDF rendering; migration/rollback; backend transport documentation and separately
owned mobile compatibility. Corrections do not substitute a narrow credit-only
review for that matrix. No independent reviewer was launched, no acceptance or
hosted ARM64/image/lifecycle success is inferred from local source tests.


### Fresh local validation and limits

- Before correction: 126 passed / 42 failed, zero skips. This reproduces all 36
  ordinary CI failures plus four null/omitted-contributor service crashes, the
  attribution rewrite and blank-owner rendering. Named-owner and native
  coincidence controls passed. Original logs/TRX remain retained.
- Corrected affected selection: 188 passed, zero skips. Ordinary .NET selection
  then ran once: 5,955 passed / 18 failed, zero skips. Every remaining failure is
  an unchanged `ReleaseBundleTests` fixture creating group-writable directories
  under this host's `umask 0002`; production correctly refuses those directories.
  All 33 release-bundle cases passed under `umask 0022`, with no source/assertion
  changes. The ordinary receipt includes the subsequently removed redundant
  foreign-warmup test; the final test-only delta (removal, exact tag inventory,
  explicit valid-v3 admission) passed 52 cases. Counts overlap, not additive.
  No complete ordinary-suite green rerun is claimed.
- Client selection: 561 passed, zero skips; typecheck passed. The live title/fallback
  test uses the existing resolver/Leaflet layer seam. An initial test wired to the
  obsolete adapter helper exposed an incomplete mock; it was replaced by the actual
  presentation-owner test, not treated as a product failure.
- Sequential Vite build, built-asset checks, Release build/publish and the exact
  published-directory asset graph passed. Existing compiler warnings remain. No
  concurrent Debug/Release build wrote this worktree's static-asset prerequisites.
- Exactly one fresh managed author/public browser smoke passed without a retry.
  Both updated screenshots were inspected: inherited-terms help is readable and
  public notices visible. Fixture and environment cleanup verification passed.
  Blank-owner cases remain compiled-Razor evidence; no expanded browser matrix.
- Compose tooling: 32 passed; browser/tool lifecycle tests: 43 passed. CI-tooling
  discovery ran 56 tests with one skip; two Windows cache-selection methods cannot
  launch missing `pwsh` (14 subtest errors). Focused Rust admission/backend/ordering
  and host-trust refusal/positive controls passed. No Windows-runtime claim.
- Rustup 1.29.3 passed local host-CA and manager admission. Explicit rustls/no-curl
  toolchain checking reported existing Rust 1.90.0 unchanged. This is a guarded
  local channel check, not fresh compiler acquisition or hosted ARM64 execution.
- Docker is absent, so existing image/Compose integration cannot execute locally.
  No image build, lifecycle, container PDF/SSE or hosted ARM64 success is inferred
  from the passing tooling/service checks. Hosted exact-head CI remains pending.

Code Guard retains the shipped policies and allowances. Complete pinned-base scope
reports REVIEW only: existing controller/DTO/view responsibilities, cohesive draft,
parser/transaction/export and map owners, grouped behavioral fixtures and the
Compose qualification sequence remain readable at their current boundaries. The
single nested bounds traversal and distinct protocol/domain branches justify the
nesting/complexity reviews; API/changelog remain navigable reference/history.
No mechanical splitting, exclusion, threshold, baseline or assertion weakening.
The obsolete foreign-fetch test was removed because the behavior is forbidden, and
the unused adapter renderer was removed because it was not the live hover owner.
