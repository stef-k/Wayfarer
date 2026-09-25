# Stable application-image publication

The #642 pipeline publishes only `ghcr.io/stef-k/wayfarer` for `linux/amd64`, using
[the accepted application Dockerfile and runtime checks](26-Application-Container.md).
Compose, Caddy, `wayfarerctl`, final `release.json` and the version-matched deployment
bundle remain later #603 work. This image is not yet a complete supported self-hosting
distribution. The PostgreSQL/PostGIS image decision remains unresolved downstream.

## Authorization and identity

`.github/workflows/application-release.yml` runs only on a published non-draft,
non-prerelease GitHub Release in `stef-k/Wayfarer`. Prepare an actual release through
the existing version/tag/release process; do not create a stable release for testing.
The workflow creates or retargets neither Git tags nor GitHub Releases.

Before building, `tools/release/version.py` requires:

- strict `vX.Y.Z`, matching `Version.props` and its derived compiled-version properties;
- the event's full source SHA equal to checked-out HEAD and the local tagged commit;
- the remote tag resolve to that same commit (including annotated tag peeling);
- a clean source checkout and an existing published stable GitHub Release whose tag
  and name both equal `vX.Y.Z`, following the existing release-tool contract.

Checkout uses the event SHA, never moving main. These checks run again before push.
The root Dockerfile builds a Release linux-x64 application; OCI source, full revision
and version labels are generated from the validated identity. Running `version` in
the built image must report exactly `Wayfarer X.Y.Z`. Labels aid inspection; the
registry digest is the immutable distribution identity.

Only the publication job receives `packages: write`; all jobs have `contents: read`.
Authentication uses the scoped `GITHUB_TOKEN`, passed through stdin, with logout on
completion/failure. No PAT, secret build argument, release-asset write or admin scope
is required. New release-path Actions are pinned to commit SHAs. PR image validation
has read-only permissions and calls only the non-push dry-run command.

## Stable tags and evidence

Only `ghcr.io/stef-k/wayfarer:vX.Y.Z` is pushed; there is no `latest`. Publication is
serialized by release tag and checks registry absence both before building and
immediately before push. Existing tags, authentication failures and ambiguous registry
errors fail closed. A dependency/base patch requires a new Wayfarer version, not a
rerun that overwrites the old image. Repository/package administrators must also
preserve tags: GHCR does not provide a conditional create-only push, so unrelated
writers must not race this workflow or retarget/delete stable images.

The pipeline deliberately pushes a single AMD64 manifest, with provenance/SBOM index
creation disabled. `manifestDigest` and `platformDigest` therefore agree. It checks
that this digest's manifest references the tested image config. No index or additional
architecture is supported by this slice.

Download the `image-publication` and successful `image-evidence` Actions artifacts:

```sh
gh run download RUN_ID -n image-publication -D /absolute/evidence/publication
gh run download RUN_ID -n image-evidence -D /absolute/evidence/qualified
```

The JSON records image/tag, source URL/full SHA, compiled version, platform,
manifest/platform/config digests, pinned Dockerfile base images, workflow run/attempt
and qualification status. Publication evidence is retained before the anonymous gate;
only `anonymous pull and smoke passed` records successful distribution qualification.
Actions retention applies: preserve these artifacts with the real release evidence.
This is image-specific evidence for the future bundle owner, not the final
`release.json` schema or an incomplete deployment archive.

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

**Pending acceptance:** no real stable package was published to test #642. The first
subsequent real stable release must prove actual GHCR push, repository linkage,
public visibility, anonymous exact-digest pull and the resulting qualified evidence.
Dry-run/CI success does not satisfy that production-publication gate. Release notes
for that release must retain the application-image-only stage until #603's bundle
and lifecycle work is accepted.

The production Compose substrate is now described in [Compose deployment](28-Production-Compose.md);
its managed/external topology does not complete the later guided lifecycle product.
