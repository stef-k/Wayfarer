---
title: Publication
---

# Publication

This page owns application image/index publication, anonymous distribution
qualification, stable bundle/bootstrap publication, public installation acceptance
and the post-publication lifecycle witness for Linux AMD64 and ARM64.

Start with [Versioning](versioning.md). Database image publication and promotion are
owned by [Production Compose](production-compose.md). Stable bundle publication consumes
that accepted PostgreSQL 18.6 + PostGIS 3.6.4 authority rather than a generic database
tag or development-server identity.

## Authorization and release identity

`.github/workflows/application-release.yml` is the stable application publication
authority. It runs only for a published, non-draft, non-prerelease GitHub Release in
the Wayfarer repository.

Before a registry write, release tooling requires the stable `vX.Y.Z` tag, checked
out source, `Version.props`, compiled application version and published GitHub
Release identity to agree. Application publication also validates the committed
accepted database evidence before building or pushing.

The workflow checks out the exact release source. It does not create or retarget Git
tags or GitHub Releases.

Publication permissions are narrow: image/index writers receive package write access;
the release-asset step receives contents write access; qualification jobs remain
read-only where possible.

## Acquisition prerequisites

Every application publication/qualification job and database publication host job
corrects signed-APT CA packages and runs the existing host trust guard before its
first workflow-controlled setup, API or registry-authentication operation. Public
lifecycle/operator jobs establish the same prerequisite on their fresh runners.
The maintainer accepts GitHub provisioning, runner/action transport, source
resolution and artifact services as platform trust roots; this does not certify
pre-step TLS stores or earlier downloads. See the
[security boundary](../development/security.md#hosted-platform-and-workflow-acquisition).

The public Compose job intentionally has no source checkout. Signed APT correction
and an inline copy of the existing host-only guard establish trust before downloading
the public bootstrap. Its expected SHA256 still comes from the publishing job's
retained artifact receipt and must pass before extraction/execution. Subsequent
public bundle and manifest integrity checks remain required.

These prerequisites do not authorize publication. #915's Caddy replacement has a
separate compatibility and native-qualification preflight below. Release identity,
accepted database authority and create-only publication retain their existing gates.

## Stable image identity and create-only publication

The stable application image is a multi-platform GHCR release with native AMD64 and
ARM64 manifests. Native publication first builds and qualifies each architecture,
then a separate index step binds the exact qualified native digests.

The registry digest is distribution identity. OCI source/revision/version labels aid
inspection but do not replace the digest.

Stable publication is create-only:

- a stable tag must be absent before publication;
- the workflow rechecks absence before the write;
- an existing or ambiguous identity fails closed;
- a dependency/base refresh requires a new application version;
- an interrupted publication is reconciled from retained evidence rather than by
  overwriting or rebuilding the existing stable identity.

Wayfarer does not publish a mutable application `latest` image tag.

### Compatibility before the first write

Both `image.py publish` and `image.py index` run the read-only `caddy.preflight`
before build/tag/push/index operations. It discovers the supported public
predecessor, checks both native archives against GitHub asset sizes/digests and
their payload inventories, and validates the exact database, templates and ordered
migration prefix. Bundle assembly still repeats the final exact-manifest boundary.

For the v1.9.22 Caddy bridge, the shared `caddy-replacement.json` policy permits only
the reviewed old/new Compose hashes; other templates remain exact. Preflight also
requires `caddy-replacement-amd64` and `caddy-replacement-arm64` artifacts from one
successful `tests.yml` run, with matching source workflow identity, complete
compatibility-input hash, exact public source fingerprints, target/recovery owner
and old/new TLS readability. A generic green job is insufficient. Discovery is
bounded to the latest 30 successful source runs; missing, expired, incompatible or
unavailable evidence fails closed before publication. There is no publication bypass.

Qualification hashes product/tooling inputs separately from release numbering and
prose, allowing a version-only release preparation or merge commit to reuse exact
unchanged compatibility evidence. Native job logs and receipts identify the actual
source/candidate heads and fixture authority. This candidate proof is not stable
public acquisition or production installation acceptance. New stable releases still
need normal version, independent-review, publication and public-acceptance gates.

## Anonymous qualification

After each native image is published, a fresh runner without package-write permission
uses an empty Docker client configuration to pull exactly the recorded digest.

It verifies native platform, OCI identity, compiled version and the immutable
application/browser runtime checks. The image is never rebuilt in the anonymous job.

A successful push is therefore not yet a public-distribution claim. Anonymous
exact-digest pull and qualification must also succeed. If package visibility prevents
anonymous access, correct the package visibility and rerun only the failed read-only
qualification path; do not rerun a successful create-only publisher.

## Stable bundle and bootstrap publication

Only after application-image qualification does stable bundle publication proceed.

Stable assembly:

- consumes the accepted production database authority from
  `tools/release/database-release.json`;
- selects the exact application native manifest from the published index;
- validates the bundled operator/recovery payload;
- emits a versioned deployment archive and checksum for each native platform;
- emits the small platform bootstrap archive containing the exact bundled
  `wayfarerctl` executable and its checksum;
- records publication evidence for the intended bytes.

Release assets are create-only. Existing versioned archives, checksums or bootstrap
asset names block republishing instead of being replaced. No mutable bundle alias is
introduced.

The public bootstrap is an acquisition entry point; it is not a second application
build.

## Non-mutating validation

PR/source validation uses the same release tooling without stable publication:

~~~sh
PYTHONDONTWRITEBYTECODE=1 uv run --with pytest==8.3.5 python -m pytest tools/release/tests
python3 tools/release/image.py dry-run --output /absolute/evidence/image-dry-run.json
~~~

The dry run builds/qualifies locally and emits candidate evidence. It does not log in,
push an image, create a release, publish assets or establish public availability.

Candidate bundle qualification similarly proves candidate integrity/compatibility
only. Keep candidate and stable claims distinct.

## Public operator acceptance

After stable assets exist, public acceptance runs from fresh native AMD64 and ARM64
runners using the public bootstrap and public acquisition path. It verifies the actual
published bytes rather than injecting an internal bundle, image or executable.

The acceptance boundary includes fresh guided setup and operator diagnosis against
the stable distribution. Failure is release evidence to investigate; it is not
permission to mutate an already-published identity.

Public DNS/certificate reachability and a particular production host remain
installation-specific. Fixture certificate authorities and internal test ingress do
not establish those external facts.

## Post-publication continuous lifecycle

The continuous lifecycle witness is separate from create-only publication and is
read-only with respect to release identities. It exercises a genuine supported stable
predecessor at or above the v1.9.21 floor to a later supported stable target using
released operators, bundles and images, including the required recovery/restore
boundary.

This witness proves the public lifecycle seam it actually exercises. It does not turn
candidate/PR qualification, an unsupported predecessor, or local fixture TLS into
equivalent evidence.

## Evidence boundaries

Retain publication and qualification records for the release beyond transient CI
retention when they are needed to support durable release claims. Evidence should
identify source, version, platform and immutable digests while excluding credentials
and private runtime data.

A green source/PR pipeline proves the checked source and harnesses. Stable release
claims additionally depend on the publication and public-acceptance gates above.
