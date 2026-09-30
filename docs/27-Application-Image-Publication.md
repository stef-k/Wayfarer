# Application Image and Stable Bundle Publication

This page owns application-image/index publication, anonymous qualification, stable
bundle/bootstrap assets and public installation acceptance for `linux/amd64` and
`linux/arm64`. Start with [Versioning and Release Operations](23-Versioning.md) to
choose the release path. Build/runtime detail belongs to
[the application container](26-Application-Container.md); immutable identity/trust
semantics belong to [the release contract](25-Container-Release-Contract.md).
DB publication and accepted evidence promotion remain with
[Production Compose](28-Production-Compose.md#derived-db-publication-and-recovery).

## Authorization and identity

`.github/workflows/application-release.yml` runs only on a published non-draft,
non-prerelease GitHub Release in `stef-k/Wayfarer`. Prepare an actual release through
the existing version/tag/release process; do not create a stable release for testing.
The workflow creates or retargets neither Git tags nor GitHub Releases. Before any
application publication, it verifies the committed accepted DB evidence; see
[accepted DB authority](25-Container-Release-Contract.md#local-release-authority-v1).

Before building, `tools/release/version.py` requires:

- strict `vX.Y.Z`, matching `Version.props` and its derived compiled-version properties;
- the event's full source SHA equal to checked-out HEAD and the local tagged commit;
- the remote tag resolve to that same commit (including annotated tag peeling);
- a clean source checkout and an existing published stable GitHub Release whose tag
  and name both equal `vX.Y.Z`, following the existing release-tool contract.

Checkout uses the event SHA, never moving main. These checks run again before push.
The shared root Dockerfile builds Release linux-x64 (AMD64) or linux-arm64 (ARM64); OCI source, full revision
and version labels are generated from the validated identity. Running `version` in
the built image must report exactly `Wayfarer X.Y.Z`. Labels aid inspection; the
registry digest is the immutable distribution identity.

Only native image publication and index assembly receive `packages: write`; deployment
asset publication alone receives `contents: write`. Other jobs have `contents: read`.
Authentication uses the scoped `GITHUB_TOKEN`, passed through stdin, with logout on
completion/failure. No PAT, secret build argument, release-asset write or admin scope
is required. New release-path Actions are pinned to commit SHAs. PR image validation
has read-only permissions and calls only the non-push dry-run command.

## Stable tags and evidence

The stable `ghcr.io/stef-k/wayfarer:vX.Y.Z` tag identifies one multi-platform release.
Create-only `vX.Y.Z-amd64` and `vX.Y.Z-arm64` staging tags retain its native manifests;
they are publication details, not independent version streams. There is no `latest`. Publication is
serialized by release tag and checks registry absence both before building and
immediately before push. Existing tags, authentication failures and ambiguous registry
errors fail closed. A dependency/base patch requires a new Wayfarer version, not a
rerun that overwrites the old image. Repository/package administrators must also
preserve tags: GHCR does not provide a conditional create-only push, so unrelated
writers must not race this workflow or retarget/delete stable images.

Each native runner builds and qualifies one manifest with automatic provenance/SBOM
indexes disabled. Fresh native anonymous-pull jobs repeat the same browser smoke.
Only after both succeed does `publish-index` join those exact digests and verify its
AMD64/ARM64 descriptors. Bundles record the shared index in `ApplicationDigest` and
the exact executable manifest in `PlatformDigest`; Compose and helpers execute only
that selected manifest. The index does not create another lifecycle authority.

Download the `image-publication` and successful `image-evidence` Actions artifacts:

```sh
gh run download RUN_ID -n image-publication-amd64 -D /absolute/evidence/publication-amd64
gh run download RUN_ID -n image-publication-arm64 -D /absolute/evidence/publication-arm64
gh run download RUN_ID -n image-evidence-amd64 -D /absolute/evidence/qualified-amd64
gh run download RUN_ID -n image-evidence-arm64 -D /absolute/evidence/qualified-arm64
gh run download RUN_ID -n image-index -D /absolute/evidence/index
```

The JSON records image/tag, source URL/full SHA, compiled version, platform,
manifest/platform/config digests, pinned Dockerfile base images, workflow run/attempt
and qualification status. Publication evidence is retained before the anonymous gate;
only `anonymous pull and smoke passed` records successful distribution qualification.
Actions retention applies: preserve these artifacts with the real release evidence.
These are image-specific records. The deployment publication artifacts below own
canonical bundle and bootstrap byte evidence.

## Anonymous qualification and first publication

A separate fresh runner, without package-write permission, creates an empty Docker
client configuration and pulls `ghcr.io/stef-k/wayfarer@sha256:...` using exactly the
publication job's digest. It checks platform, labels and compiled version, then runs
the same non-root, read-only payload, absent-build-tools, static-assets and real
Chromium PDF checks used before push. Smoke containers have no network or database;
Chromium must already be installed. Full prepared-database qualification remains
with #640 and the later Compose child.

[GHCR initially creates packages as private](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry).
Publishing with this repository's token and OCI source label links the package to
Wayfarer. A maintainer must set the package visibility to **public** in GitHub package
settings if anonymous pull is denied. Public repository visibility alone is insufficient.
The workflow fails rather than claiming public qualification or requesting a PAT.
After correcting visibility, rerun **only the failed anonymous-pull job**. Do not rerun
the successful publishing job: an existing stable tag is intentionally rejected.

For read-only recovery after an interrupted workflow, use a clean checkout of the
exact release commit, with its tags fetched and the recorded digest (not a fresh build):

```sh
python3 tools/release/image.py qualify --tag vX.Y.Z --source FULL_SOURCE_SHA \
  --digest sha256:RECORDED_DIGEST --output /absolute/evidence/image-evidence.json
```

`gh` must be able to read the GitHub Release; its credentials are not passed to the
anonymous Docker client. Obtain the digest from trusted workflow evidence, not an
untrusted mirror. A push interrupted before digest recording requires registry/run
inspection; never delete, overwrite or rebuild the stable tag to recover a test.

## Non-mutating validation and acceptance

```sh
PYTHONDONTWRITEBYTECODE=1 uv run --with pytest==8.3.5 python -m pytest tools/release/tests
python3 tools/release/image.py dry-run --output /absolute/evidence/image-dry-run.json
```

The dry run uses the same Dockerfile, labels, compiled-version check and smoke script,
but never logs in or pushes. It emits null registry digests and `local dry-run only`;
its local tag is not release evidence. Unit tests exercise stable source rejection,
registry overwrite/error handling and digest evidence. PR CI runs both paths without
creating fake releases or changing registry state. No database migration is introduced.

Dry-run/CI success does not prove public distribution. A genuine stable release must
retain successful GHCR publication, repository linkage, anonymous exact-digest image
qualification and [public Compose acceptance](#stable-compose-distribution).
Current user-facing availability belongs to
[Install & Self-Hosting](02-Install-and-Dependencies.md#availability).

## Local candidate bundle consumer

The local bundle assembler now consumes the same version and OCI identity owners,
requiring an actual locally available immutable application digest and exact source.
Candidate archive names contain `candidate`, version and full source SHA. Stable
archives use `wayfarer-vX.Y.Z-linux-amd64.tar.gz` or the ARM64 equivalent, with a versioned `.tar.gz.sha256` sidecar.
Local integrity does not establish publisher authenticity, anonymous pull availability
or GitHub asset provenance. The release workflow publishes stable bundles after anonymous image
qualification; public acquisition and the operational acceptance gate are described below. See [local bundle assembly](25-Container-Release-Contract.md#local-release-authority-v1).


## Stable Compose distribution

Explicit `python3 tools/release/bundle.py --stable --tag vX.Y.Z --source FULLSHA
--app-digest sha256:... --output /absolute/new-output`
reuses `version.py` and `image.py`: clean HEAD, local/remote tag, Version.props and
published non-draft/non-prerelease Release must agree. The already-published application
index/selected manifest/config must match the exact digest and native Linux platform.
Candidate assembly remains separate and requires its explicit locally built native
DB digest; stable assembly reads `database-release.json` and rejects `--db-digest`.
Add `--publish` only in the official release workflow.

After anonymous application qualification, `application-release.yml` anonymously pulls
the accepted DB/Caddy digests, builds the self-contained payloads, validates with the
bundled operator and packages its exact native bytes into `wayfarerctl-linux-amd64.tar.gz`
or `wayfarerctl-linux-arm64.tar.gz`, containing only the executable named `wayfarerctl`. It uploads that bootstrap plus
its `.sha256` sidecar and `wayfarer-vX.Y.Z-linux-amd64.tar.gz` plus
`wayfarer-vX.Y.Z-linux-amd64.tar.gz.sha256` (and the equivalent ARM64 pair). Only this job has `contents: write`;
image publication retains `packages: write`. No PAT, release creation or mutable
`latest` bundle alias is used. Upload never uses clobber. Any occupied bootstrap,
deployment archive or sidecar identity stops **before assembly** on reruns.
Inspect the retained intended archive/checksum,
manifest and `publication.json`, compare the REST asset digest, and reconcile manually;
uncertain or different bytes cannot be rebuilt or replaced under the same identity.

The checksum sidecar supports human/offline integrity checks; it does not authenticate
a publisher. Automatic acquisition requires GitHub REST's exact lowercase
`sha256:<64 hex>` asset digest, one uploaded matching asset and bounded nonzero size.
The project/tag/name/status are fixed. The download URL is constructed from validated
identity; only GitHub's fixed HTTPS release-asset CDN may receive its redirect.

A release with no earlier deployable public bundle is a baseline with `Sources=[]`. Source-only
v1.9.19 and earlier releases remain untouched. Later publication locates the highest
earlier stable that advertises the deployment asset, validates its public digest,
extracts with the current shipped operator and validates its stable manifest. It binds
one exact version/fingerprint/terminal migration only after DB/Caddy and topology
compatibility plus an exact ordered migration prefix and no reference seeding.
An advertised prior bundle that is invalid or incompatible fails publication; no silent
source omission or search for a different update target occurs.

The real `public-compose-acceptance` job downloads the public bootstrap tarball on a
fresh native runner and checks it against the retained publication evidence's verified
asset digest before extraction. It runs setup without bundle/version/digest arguments;
that anonymous latest-stable path acquires the canonical bundle and exact images,
then exercises representative external Compose setup and retained-dispatch doctor/stop.
The extracted bootstrap executable must match the acquired bundle's operator bytes.
Publication evidence records tag/source/platform/operator hash/bundle fingerprint/all
asset digests/image digest; the public job retains setup/acquisition evidence. PR CI covers deterministic
logic and the existing candidate recovery/update journey. It creates no stable release.
Acceptance requires all native publication/anonymous-pull, index, deployment-bundle
and `public-compose-acceptance` jobs to succeed on both supported platforms. Preserve
`deployment-publication-{amd64,arm64}`, `deployment-publication-evidence-{amd64,arm64}`
and `public-compose-acceptance-{amd64,arm64}` alongside image evidence beyond Actions
retention. A baseline claims no stable-to-stable migration; candidate real-migration
evidence remains separate until a second compatible genuine public release supplies
that proof.
