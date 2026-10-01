# Versioning and Release Operations

Start here before preparing versions, changing release metadata or publishing images,
bundles and bootstrap assets. This page owns maintainer orchestration and version
preparation; follow the specialized owners below when a step needs implementation
or operator detail. Workflows and release tools define executable behavior. If that
behavior contradicts the accepted architecture, report the conflict rather than
turning it into a new documentation contract.

## Release authority map

| Owner | Classification | Responsibility |
| --- | --- | --- |
| This page | Maintainer release procedure; historical provenance | Version preparation, choosing and sequencing a release path, retained source records |
| [Container and release contract](25-Container-Release-Contract.md) | Normative contract | Immutable identities, trust, compatibility and canonical `release.json`/bundle semantics |
| [Application container](26-Application-Container.md) | Container implementation contract; historical evidence | Application build/runtime, maintenance and disposable image qualification |
| [Application-image publication](27-Application-Image-Publication.md) | Maintainer release procedure | Native app images/index, anonymous qualification, stable bundles/bootstrap assets and public acceptance |
| [Production Compose](28-Production-Compose.md) | Compose contract; maintainer DB procedure; historical evidence | Topology, DB recipe/publication/qualification and accepted DB evidence promotion |
| [wayfarerctl operator guide](29-Wayfarerctl.md) | Operator procedure; historical qualification evidence | Acquisition, setup, dispatch, update and recovery after artifacts exist |
| [Shipped INSTALL](../tools/release/INSTALL.md) | Operator procedure | Using the bundle in hand, including local/offline placement |
| [README](../README.md), [Install & Self-Hosting](02-Install-and-Dependencies.md), [Deployment](20-Deployment.md) | User-facing overview; installation/deployment procedures | Availability, installation choices and native/manual deployment |
| [Workflows](../.github/workflows/) and [release tools](../tools/release/) | Executable authority | The implemented checks, dispatch inputs, publication and assembly behavior |

The accepted stable DB publication authority is
[`tools/release/database-release.json`](../tools/release/database-release.json),
not the current candidate recipe. Its promotion procedure belongs to
[derived DB publication and recovery](28-Production-Compose.md#derived-db-publication-and-recovery);
its immutable selection and bundle semantics belong to
[the release contract](25-Container-Release-Contract.md#local-release-authority-v1).

## Choose the release path

### Application-only release

1. Prepare and validate the application version using [the release helper](#release-helper).
   Keep accepted DB evidence unchanged when the DB baseline is unchanged.
2. Obtain independent review, successful exact-head CI and maintainer acceptance of
   the release source under [repository rules](../AGENTS.md#commit--pull-request-guidelines).
   Tag that accepted commit and intentionally publish its matching stable GitHub
   Release only with release authorization.
3. Follow [application publication](27-Application-Image-Publication.md#authorization-and-identity).
   Publishing the Release triggers the existing workflow: accepted DB verification,
   native app publication/anonymous qualification, index assembly, canonical
   bundles/bootstrap assets, then fresh public installation acceptance on both platforms.
   [Stable Compose distribution](27-Application-Image-Publication.md#stable-compose-distribution)
   owns these gates and interrupted-publication recovery.
4. Preserve the workflow evidence with the release. Operators then use
   [acquisition and lifecycle commands](29-Wayfarerctl.md); publication does not
   deploy or authorize an update to an existing installation.

### DB-only refresh, publication and promotion

1. Review and merge the candidate recipe change under
   [the DB contract](25-Container-Release-Contract.md#database-contract) and
   [Compose image qualification](28-Production-Compose.md#exact-third-party-image-decision).
2. With explicit DB publication authorization, follow
   [exact-source dispatch](28-Production-Compose.md#maintainer-activation-and-exact-source-dispatch).
   The DB workflow publishes and anonymously qualifies native manifests, then binds
   their immutable index. It does not create an application version/tag/Release.
3. Independently review the resulting evidence and merge a separate
   [evidence-promotion PR](28-Production-Compose.md#accepted-db-evidence-promotion)
   for `database-release.json`. Publication alone does not change accepted authority.
   Later application releases consume the promoted authority; existing installations
   retain the DB identity in their own bundles.

### Combined DB and application release

Complete the DB-only path, including reviewed evidence promotion, **before** tagging
and publishing the application release source. Then follow the application-only
path from a commit containing that accepted DB evidence. Do not substitute a candidate
DB recipe or a mutable registry tag for the promoted artifact. DB-major migration
and PostGIS extension upgrades require their separately tested contracts; the release
sequence does not authorize either operation.

## Public acceptance and availability

[Install & Self-Hosting](02-Install-and-Dependencies.md#availability) owns current
user-facing asset availability. Source-only v1.9.19 and earlier are historical releases,
not Compose update sources. For the first public Compose stable, require successful
[public distribution acceptance](27-Application-Image-Publication.md#stable-compose-distribution)
on AMD64 and ARM64 before claiming installation support. A baseline with no update
sources cannot prove stable-to-stable migration; that requires a subsequent compatible
public release. Candidate/PR qualification remains separate evidence.

## Application version source

`Version.props` is the runtime and release version source. The root file contains
the manually edited `WayfarerVersion` and derives standard MSBuild metadata from it:

```xml
<WayfarerVersion>1.9.19</WayfarerVersion>
<Version>$(WayfarerVersion)</Version>
<PackageVersion>$(WayfarerVersion)</PackageVersion>
<AssemblyInformationalVersion>$(WayfarerVersion)</AssemblyInformationalVersion>
<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
```

`IAppVersionProvider` reads the compiled `AssemblyInformationalVersion`. The `version`
CLI, `GET /api/version`, `X-Wayfarer-Version` and shared layout footer use that provider.
Validate exact CLI output with `dotnet run --no-launch-profile -- version`; for the
version above it prints exactly `Wayfarer 1.9.19`, without SDK launch-profile messages.

## Release helper

Use the repo-local helper to prepare and validate release metadata:

```sh
python3 tools/release/version.py prepare <next-version>
python3 tools/release/version.py check
python3 tools/release/version.py check --require-tag
python3 tools/release/version.py check --require-github-release
```

`prepare` updates only `WayfarerVersion` and adds the target changelog skeleton.
One exact leading `## [Unreleased]` is supported: `check` validates the first dated
release against `Version.props`, and `prepare` inserts the new release below the
complete Unreleased section and above released history. Without leading Unreleased,
it inserts after the title as before. Unreleased notes and prior releases remain
intact; consolidating the notes into the new release is a maintainer step. Default
`check` is offline; tag/GitHub checks run only with their explicit flags. The helper
never creates, edits, publishes or deletes tags or GitHub Releases.

For version preparation, run the release-helper tests, focused compiled Versioning
tests and exact CLI output check, inspect prior-note preservation and diff hygiene,
and run Code Guard over the branch. Product evidence can be retained from the reviewed
product head when the version-only delta is outside that qualification's scope;
current-head CI and independent review still apply. Follow
[publication validation](27-Application-Image-Publication.md#non-mutating-validation-and-acceptance)
when the image/release boundary changes.

## Historical source-release provenance

The records below describe source releases prepared before public Compose distribution.
[CHANGELOG](../CHANGELOG.md) owns feature and upgrade notes; linked product PRs/CI
own detailed change and review evidence. Records retain preparation source, exceptional
migration/deployment requirements, independent review and meaningful qualification limits.
Standard release validation is described once above. Native deployment follows
[Updating Wayfarer](20-Deployment.md#updating-wayfarer): apply pending migrations and
preserve PostgreSQL with its matching complete Data Protection ring. Publication
never performs that deployment. These historical records are not current Compose procedures.

## 1.9.19 release source record

Prepared on 2026-09-22 from synchronized main `5ed49f8e`.
[PR #606](https://github.com/stef-k/Wayfarer/pull/606) repairs the v1.9.18 public
Timeline failure from legacy Hidden Area SRID 0; feature details remain in CHANGELOG.

Apply `20260922182107_RepairHiddenAreaSrid` with the corrected application. It labels
only SRID-0 polygons without moving coordinates, preserves 4326 and aborts unexpected
nonzero SRIDs; Down retains corrected metadata. The exceptional
[upgrade sequence](20-Deployment.md#hidden-area-srid-correction-after-v1918) stops
old writers **before** migration because the deployment script normally stops after
migrations. Independent review at `3580b6cf` reported 41 passing tests, zero skipped,
including 15 PostgreSQL migration/public-recovery cases; exact-head `test` CI passed
before merge. No API shape, dependency or Mobile change was introduced.

## 1.9.18 release source record

Prepared on 2026-09-22 from synchronized main `cd35db48`.
[PR #602](https://github.com/stef-k/Wayfarer/pull/602), closing #601, is the only delta
since v1.9.17. Public Timeline/embed statistics share privacy eligibility; private
owner statistics are unchanged. Existing timestamp conventions remain in
[Timeline statistics](06-Timeline.md#statistics-grouping).

Independent review, 104 focused passing tests and exact final-head `test` CI are
retained from PR #602. No migration, API shape, dependency or Mobile change since
v1.9.17; reload open pages after deployment.

## 1.9.17 release source record

Prepared on 2026-09-17 from main `e6b8bd48` with the shared embed cursor correction
on `fix/embed-full-view-cursor`. Public Trip/Timeline full-view links use the pointer
cursor; existing URL/iframe copy actions were unchanged and confirmed visible by
the maintainer. CHANGELOG retains the correction.

Five shared embed client tests passed. No fresh mounted-browser hover verification
is claimed. The cohesive changelog was accepted despite its size review. No migration,
API, dependency or Mobile change since v1.9.16; reload open pages after deployment.

## 1.9.16 release source record

Prepared on 2026-09-15 from synchronized main
`4feebd2773008d6b5c67378c540939c0368c6cd1` on `feature/release-1.9.16`.
[PR #597](https://github.com/stef-k/Wayfarer/pull/597), closing #596, is the only
first-parent delta since v1.9.15; CHANGELOG owns the cooperative embed feature notes.

Independent product and focused thumbnail re-review passed at
`e107451a47fadd31e0cba368b35ed8a64834fe43`; exact-head
[test CI](https://github.com/stef-k/Wayfarer/actions/runs/35014379085) passed.
Evidence includes three mounted iframe browser tests, Chromium mobile emulation,
and 20 thumbnail plus five shared embed tests in final re-review. The thumbnail
browser uses real Leaflet/production capture with fixture HTML, not a live-DB journey;
physical devices and Safari were not qualified. No migration, API, dependency or
Mobile change since v1.9.15. Existing embed URLs remain valid; copying embed output
requires public HTTPS and open pages should be reloaded after deployment.

## 1.9.15 release source record

Prepared on 2026-09-14 from synchronized main
`44ed5356edb68fc2427479643455084350b3049c` on `feature/release-1.9.15`.
v1.9.14 resolved to `6a6251506110ba95c6bdaf7179249801ed0b16cb`. First-parent delta:

| Commit | Product PR / retained CI |
| --- | --- |
| `a790b001` | [#591](https://github.com/stef-k/Wayfarer/pull/591), closes #590; [run 34384832416](https://github.com/stef-k/Wayfarer/actions/runs/34384832416) at `fb44dd13fc18d9b17f6a8593a2d385d2dfca851e` |
| `44ed5356` | [#593](https://github.com/stef-k/Wayfarer/pull/593), closes #592; [run 34827297292](https://github.com/stef-k/Wayfarer/actions/runs/34827297292) at `9b9a0c64df9e94cb3b0499313d27b82e94b803c9` |

CHANGELOG owns log-root provisioning and Trip default-view capture behavior.
For #592, complete independent review and narrow P2 re-review plus eight focused
tests passed. All 10 browser cases passed across runs; seven retain earlier evidence,
and browser/build artifacts predate the narrow correction. Persistence proof covers
real PATCH, API reread and clean-URL reload; physical touch hardware was not exercised.
#590 service-start evidence remains with PR #591, not a fresh production deployment.
No migration, backend API, dependency or Mobile change since v1.9.14; reload Trip
Editor pages after deployment.

## 1.9.14 release source record

Prepared on 2026-09-07 from synchronized main `a45355cc` on `feature/release-1.9.14`.
[PR #588](https://github.com/stef-k/Wayfarer/pull/588) is the only delta since v1.9.13;
CHANGELOG owns saved Segment dirty-state and exit-confirmation details.

Independent review passed at `b8a75ff799aee319df01e4150c1c250037332187`.
Nine focused client tests, frontend typecheck/build and exact-head correction
[test CI](https://github.com/stef-k/Wayfarer/actions/runs/34133148745) passed.
Tests exercise production component state/exit guards; no mounted-browser
Save-and-leave journey is claimed. No migration, API, dependency or Mobile change;
reload open editors after deployment.

## 1.9.13 release source record

Prepared on 2026-09-06 from synchronized main `fd58c69870b9e528580abadf3f3a43f240f5cc4f`
on `feature/release-1.9.13`; latest published release was verified as v1.9.12.
Only [PR #585](https://github.com/stef-k/Wayfarer/pull/585) (#584 atomic proposal Save)
and [PR #586](https://github.com/stef-k/Wayfarer/pull/586) (#583 preview/provider-mode
refinements) appear in the first-parent delta. Migration files/snapshot were unchanged;
latest migration remained `20260905095140_AddLocationProviderAddressLine1`.
The removed acceptance endpoint became ordinary validated Segment Save; reload editors.
No Mobile protocol/release change. CHANGELOG retains feature and upgrade details.

Independent product reviews covered Save, preview, planning-mode independence and
contrast. Maintainer final tuning used 80% current-route opacity, flat preview dash
ends and at most two displayed decimals; real proposals/appearance were accepted.
Full mounted dark-theme, Discard and post-Save observation is not claimed. Lower-seam
coverage/reviews found no blocker; the owned-Trip fixture 404 did not prove a Vite defect.

Final correction: 24 client tests, frontend typecheck/build and built-asset smoke passed.
Backend/PostgreSQL selections remain separately attributed in PRs #585/#586.
Exact correction-head `test` CI passed at `aceafe46a10dc46271f426e8b3b5823f35c668ca`
in [run 34058012120](https://github.com/stef-k/Wayfarer/actions/runs/34058012120).
Metadata validation included 21 helper and 26 compiled Versioning tests.
No new provider request or production operation was part of preparation.

## 1.9.12 release source record

Prepared on 2026-09-06 from synchronized main
`182665fa4bcd1431540f737063b428b98c23b898` on `feature/release-1.9.12`.
v1.9.11 resolved to `f3934de632aca388c345cc54f7a312d97978be52`.
The only first-parent delta is [PR #581](https://github.com/stef-k/Wayfarer/pull/581)
(#580 and #577 follow-up), at `182665fa4bcd1431540f737063b428b98c23b898`;
CHANGELOG retains Segment chevron/direction corrections.

### Migration boundary and deployment

Migration files/model snapshot were unchanged; latest migration remained
`20260905095140_AddLocationProviderAddressLine1`. No API, persistence, dependency
or routing-provider change. This source release introduced no binary asset workflow.

### Product evidence and limits

Reviewed commit `1bce6a75d089a708733047d6415ec0c6817539d7` was preserved on the local
correction branch. Independent combined review passed; the maintainer accepted sizing
and local direction during zoom checks. Evidence records 34 focused client tests,
frontend typecheck/build and built-asset smoke. The historical 6,308-vertex Ella-to-Kandy
replay remains independently unverified because its artifact was unavailable;
mirrored synthetic tight-return tests supply reproducible direction proof.
Exact reviewed correction-head
[test job 101515334540](https://github.com/stef-k/Wayfarer/actions/runs/34043873977/job/101515334540)
passed before squash merge; release-PR CI was a separate gate.

### Release validation scope

The metadata diff was limited to `Version.props`, `CHANGELOG.md`, this document and
`AppVersionProviderTests.cs`. Both correction notes moved into the dated release;
prior notes and one empty Unreleased section were preserved. Product suites were not
repeated locally for metadata-only preparation.
