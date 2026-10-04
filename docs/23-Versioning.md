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
| This page | Maintainer release procedure | Version preparation, choosing and sequencing a release path, public acceptance policy |
| [Container and release contract](25-Container-Release-Contract.md) | Normative contract | Immutable identities, trust, compatibility and canonical `release.json`/bundle semantics |
| [Application container](26-Application-Container.md) | Container implementation contract | Application build/runtime, maintenance and disposable image qualification |
| [Application-image publication](27-Application-Image-Publication.md) | Maintainer release procedure | Native app images/index, anonymous qualification, stable bundles/bootstrap assets and public acceptance |
| [Production Compose](28-Production-Compose.md) | Compose contract; maintainer DB procedure | Topology, DB recipe/publication/qualification and accepted DB evidence promotion |
| [wayfarerctl operator guide](29-Wayfarerctl.md) | Operator procedure | Acquisition, setup, dispatch, update, recovery and qualification boundaries |
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
user-facing asset availability. **v1.9.21 is the first supported Compose/`wayfarerctl`
lifecycle and update baseline; v1.9.22 is the current stable release.** v1.9.20 remains
an immutable transitional public Compose artifact set, not a supported installation
or update source.

Release tooling gives v1.9.21 no required supported predecessor. For later
stable targets it selects the highest earlier deployable stable release >=v1.9.21,
then applies the existing exact-byte/manifest and compatibility checks. If no eligible
supported predecessor exists, or its bytes are invalid or authority incompatible,
publication fails closed. Neither v1.9.20 fallback nor a new empty `Sources` baseline
is permitted.

Current operator builds refuse exact public acquisition below v1.9.21; `latest` and
supported exact selectors keep their existing identity/digest/bundle validation.
Already-published v1.9.21 operator bytes may still accept an explicit v1.9.20 request,
and its manifest historically records `Sources=[v1.9.20]`. Those immutable bytes,
metadata and tags remain immutable.

Source-only/native v1.9.19 and earlier are not Compose update sources. Maintainer
native/systemd v1.9.19 uses [#604's explicit native-to-Compose migration](https://github.com/stef-k/Wayfarer/issues/604)
directly to the supported baseline, without a transitional v1.9.20 step. Operator
protocol minimums and historical recovery schema/version floors remain separate
compatibility authorities; the Compose support floor does not raise them.

Require [public distribution acceptance](27-Application-Image-Publication.md#stable-compose-distribution)
on AMD64 and ARM64 before claiming installation qualification. Continuous lifecycle
qualification must use **v1.9.21 → a later supported stable** with the actual released
operators and bundles, a selected verified archive and a clean restore. Candidate/PR
qualification and the unsupported v1.9.20 → v1.9.21 boundary do not supply that witness.

The read-only, AMD64-only
[post-publication lifecycle workflow](27-Application-Image-Publication.md#post-publication-continuous-lifecycle)
owns that witness separately from create-only publication. The genuine public
**v1.9.21 → v1.9.22** lifecycle has passed, including clean restore and account recovery.
Its stable update has zero EF migration delta; candidate qualification retains real
migration and failure coverage. Public setup acceptance on both platforms and the
[public operator witness](29-Wayfarerctl.md#public-stable-operator-evidence) have also passed.
Retain and independently review each qualification's artifacts beyond Actions retention.
PR CI proves the qualification tools and applicable product seams; public runtime
acceptance requires the actual published bytes. Fixture-controlled HTTPS and local
test-CA managed ingress do not prove public DNS/CA reachability, production-host
fitness, native migration or a complete ARM64 lifecycle.

[CHANGELOG](../CHANGELOG.md) and [release notes](https://github.com/stef-k/Wayfarer/releases)
own shipped change history. GitHub issues/PRs, Actions artifacts and git history retain
design, review, qualification evidence and exact source lineage.

## Application version source

`Version.props` is the runtime and release version source. The root file contains
the manually edited `WayfarerVersion` and derives standard MSBuild metadata from it:

```xml
<WayfarerVersion>1.9.22</WayfarerVersion>
<Version>$(WayfarerVersion)</Version>
<PackageVersion>$(WayfarerVersion)</PackageVersion>
<AssemblyInformationalVersion>$(WayfarerVersion)</AssemblyInformationalVersion>
<IncludeSourceRevisionInInformationalVersion>false</IncludeSourceRevisionInInformationalVersion>
```

`IAppVersionProvider` reads the compiled `AssemblyInformationalVersion`. The `version`
CLI, `GET /api/version`, `X-Wayfarer-Version` and shared layout footer use that provider.
Validate exact CLI output with `dotnet run --no-launch-profile -- version`; for the
version above it prints exactly `Wayfarer 1.9.22`, without SDK launch-profile messages.

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
