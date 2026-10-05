# Release bundle v1

This directory is a candidate qualification artifact unless `release.json` explicitly
records stable status. Checksums prove integrity, not publisher authenticity. Obtain
these bytes from a trusted administrator/maintainer or the exact public stable Release.
The target host needs native Linux AMD64 or ARM64, Docker/Compose and root, not an SDK.

The first supported Compose lifecycle/update baseline is v1.9.21. v1.9.20 is an
immutable transitional publication, not a supported installation/update source.
Future operators refuse exact public acquisition below v1.9.21; `latest` is unchanged.
Already-published v1.9.21 operator bytes may still accept 1.9.20, and its manifest
historically names v1.9.20 in `Sources`; neither is rewritten. Operator/recovery
protocol floors are independent. Native/systemd v1.9.19 uses #604's explicit migration
directly to the supported Compose baseline, not `wayfarerctl update`.

When consulting repository documentation on GitHub, select the exact `SourceRevision`
recorded in this bundle's `release.json`, or its matching stable `Tag`. Repository
file references below refer to that source revision, keeping the guidance aligned
with the bundled operator.

Use a previously trusted operator to run `release inspect /absolute/bundle` and
`release verify-images /absolute/bundle`. Missing local images mean the bundle is not
execution-ready. These commands never pull images. Offline import accepts an
already-extracted trusted directory. `release unpack ARCHIVE
EMPTY_PRIVATE_STAGE` safely extracts to `STAGE/bundle` without importing or pulling.
Candidate checksums remain in `SHA256SUMS`; stable archives use a versioned `.sha256` sidecar.

Create a root-owned mode-0700 deployment root if it does not exist. Run
`wayfarerctl --deployment-root /etc/wayfarer release import /absolute/bundle`.
Import copies to private staging and publishes without replacing existing releases.
It does not change the current installation, start services or migrate a database.
A collision with different bytes fails. No release/image garbage collection exists.

For a completed matching installation, explicitly run `release adopt /absolute/bundle`
using the exact bundled operator. Adoption validates actual bundle, image and available
recovery evidence before adding installation schema-4 release authority. UUID, secrets,
project, hostname, backup policy, storage and runtime inputs remain unchanged.

Keep a trusted bootstrap operator at a fixed root-owned path, for example
`/usr/local/lib/wayfarer-bootstrap/wayfarerctl`; never overwrite a running executable.
A stable `/usr/local/bin/wayfarerctl` wrapper can exec that absolute executable with
`dispatch` followed by literal arguments. The bootstrap validates the installed bundle
and invokes only its fixed `wayfarerctl` filename. `dispatch restore --resume UUID` and
`dispatch restore --abort UUID` select the receipt's original retained operator.
A legacy receipt without an owner requires its original operator explicitly.
Do not change bootstrap bytes during ordinary lifecycle operations.

`release target /etc/wayfarer/releases/NAME project` exports independent restore target
evidence from validated retained bytes. Quartz compatibility is release-owned;
physical column order and capture snapshots are never release metadata. The target's
required snapshot field is zero (no observation), and is ignored by restore. Do not
use exported target evidence as a configured backup capture observation.

Incomplete placement retains `.stage-ID` and its protected sibling receipt. Use
`release reconcile .stage-ID` only for a complete stage. Partial stages remain clearly
non-authoritative evidence; they never select an operator or replace current bytes.

Explicit trusted-local update is available through `update --bundle PATH --plan`
and `update --accept-plan SHA256`; use `update --resume UUID` or pre-migration
`update --abort UUID` after interruption. It requires an explicitly compatible later
release, already-present images, adopted current authority and a fresh held quiesced
recovery set. Migration is forward-only in the current generation. After migration
may have started, `update --restore UUID` transfers ownership to managed restore of
the held old-release archive into a fresh generation; an old image is not rollback.
Retain current/previous bundles, operators, images, receipts and recovery holds.
Public acquisition can prepare these same inputs. See `docs/self-hosting/wayfarerctl.md`
for phase and recovery details.

Some bundles retain an independently trusted historical recovery capture pair under
`capture/`, described by `LegacyCapture` in `release.json`. Its exact files are part
of the immutable hashed inventory; do not select capture authority from an archive
manifest. Maintainer assembly requirements belong to the
local release contract in `docs/maintainer/release-contract.md`
(`Local release authority v1`).

Target export selects the historical pair when present; append `current` to select
the newly bundled pair explicitly. An adopted installation selects the profile that
matches its independently persisted capture policy. Adoption never refreshes that
policy. Prepare the complete desired inventory before import: an occupied release
name is never repaired or expanded in place. Recovery helpers are mode 0555/0444 to
satisfy the existing immutable capture-payload contract.


## Public bootstrap and ordinary updates

The primary public assets are `wayfarerctl-linux-amd64.tar.gz` and
`wayfarerctl-linux-arm64.tar.gz`, each with its matching `.sha256` sidecar on the
exact stable GitHub Release.
It contains exactly the executable `wayfarerctl`, byte-identical to this canonical
bundle's operator. It is packaged from those bytes with no second build. Native
Linux AMD64 or ARM64, matching Docker Engine, Compose v2 2.24.4+ and root are required.
The examples below show AMD64; substitute the ARM64 archive on ARM64. Raspberry Pi 5
with 64-bit Ubuntu Server follows this same generic ARM64 path. The host needs no clone, SDK/runtime, Node/npm, Python or `unzip`.
No host package or Docker daemon configuration is installed.

For a stable release, obtain its bootstrap from the official GitHub Release.
Before extraction or execution, compare its SHA-256
with the exact Release asset's REST `digest` (`sha256:<64 lowercase hex>`) at
`https://api.github.com/repos/stef-k/Wayfarer/releases/tags/vX.Y.Z`. The sidecar is
human/offline integrity evidence, not publisher authentication. Use a fresh trusted
root-owned directory for the verified bootstrap:

```sh
sha256sum wayfarerctl-linux-amd64.tar.gz   # compare against that Release asset digest
sha256sum --check wayfarerctl-linux-amd64.tar.gz.sha256  # optional sidecar check
tar -xzf wayfarerctl-linux-amd64.tar.gz
chmod +x wayfarerctl
sudo ./wayfarerctl setup
```

Bare setup validates the host, discovers latest stable through the existing public
resolver, verifies/downloads/extracts/imports the platform's canonical deployment
bundle, pulls/reverifies its exact immutable images and enters the existing setup
engine. Application/DB/Caddy identity comes from validated `release.json`, never
administrator-copied digests. Guided choices cover hostname/proxy/admin password.
No curl-pipe-shell or separate installer is used.

Setup shows each stage and retries temporary network/download failures at most three
times (normally after 2, 5 and 10 seconds; server-requested delays are capped at 30
seconds). Only discovery, the exact advertised download and exact container pulls
retry. Integrity, archive, identity and local safety failures stop immediately;
protected setup mutations never retry automatically.

When the error says **"Setup has not started"**, correct the cause and run the same
plain setup command again with your original options. Validated releases may remain,
but no installation configuration or application data was changed. Do not delete
files or use `doctor` or `setup --resume` at this boundary. GitHub failures require
internet access to github.com; container downloads require ghcr.io and the Caddy
registry (docker.io). Never bypass an integrity check; try again later from the
official release.

When it says **"Setup has started"**, retain installation files, credentials and
service data, correct the cause and run `setup --resume` with the same deployment-root
option. Continuation finishes interrupted publication of verified initial files and
checks the original protected configuration, secrets and receipt. Existing bytes
must match; credentials are never regenerated or replaced. If protected state
**"cannot safely resume"**, preserve it and have an administrator follow protected-state
reconciliation in `docs/self-hosting/wayfarerctl.md#setup-interruption`.
Another setup attempt cannot repair changed or unreceipted files.

```sh
sudo ./wayfarerctl setup --version X.Y.Z  # same resolver, one exact public stable
sudo ./wayfarerctl setup --bundle /absolute/trusted/bundle  # secondary local/offline seam
```

These selectors are mutually exclusive. The full `wayfarer-vX.Y.Z-linux-amd64.tar.gz`
or `wayfarer-vX.Y.Z-linux-arm64.tar.gz`
deployment archive plus `.tar.gz.sha256` remains supported for controlled staging,
recovery, mirrors and troubleshooting. Local setup skips discovery/download, validates
its canonical metadata/platform, derives identities, verifies already-local exact
images and uses the same import/setup owners. New canonical installs record schema-4
retained authority; separate adoption is only needed for existing matching installations.
Candidate setup derives the exact native PG18 DB manifest from canonical release
metadata. Raw-template fresh setup is rejected; stable setup accepts no digest override.

Keep the bootstrap fixed and root-owned; `wayfarerctl dispatch COMMAND` selects the
exact retained operator after installation/update transitions. The bootstrap does
not replace itself. Retain previous bundles/operators/images/receipts/recovery holds.

`release acquire X.Y.Z|latest` only prefetches validated retained bytes/images and
reports path/fingerprint/version. Anonymous GitHub/GHCR acquisition requires no
credentials and pulls only validated immutable image references, never mutable
latest tags. `update --plan` uses latest stable; `update X.Y.Z --plan` uses the exact
release. Both feed the managed plan/receipt lifecycle; `update --accept-plan
HASH` remains destructive authorization. Current/older targets, missing assets or
missing exact source compatibility fail without fallback search. Offline `release
import PATH` and `update --bundle PATH --plan` remain available during network failure.

A bundle with `Sources=[]` permits fresh setup/restore but supplies no forward-update
source. An update target must explicitly support your installed release fingerprint;
source-only releases are not implicit update sources. Inspect the bundle's actual
compatibility metadata before planning an update.

The Compose database baseline is PostgreSQL 18.6 (`18.6-1.pgdg12+2`) plus PGDG
PostGIS 3.6.4 (`3.6.4+dfsg-2.pgdg12+1`) on Bookworm. The durable DB volume mounts
`/var/lib/postgresql`; the official image owns `PGDATA=/var/lib/postgresql/18/docker`.
Use the bundle's exact native DB manifest; do not substitute an index, mutable tag,
different major or an existing PG17 physical cluster. DB-major migration is outside
ordinary setup/update. See the
operator guide in `docs/self-hosting/wayfarerctl.md`
for ongoing operation and recovery.
