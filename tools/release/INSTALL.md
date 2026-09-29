# Release bundle v1

This directory is a candidate qualification artifact unless `release.json` explicitly
records stable status. Checksums prove integrity, not publisher authenticity. Obtain
these bytes from a trusted administrator/maintainer or the exact public stable Release.
This implementation does not claim a public Compose stable has shipped; real release
acceptance remains required. The target host needs Linux AMD64, Docker/Compose and
root, not an SDK.

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
Public acquisition can prepare these same inputs; real stable release-to-release
qualification remains a separate operational gate. See the repository operator
documentation for phase and recovery details.

For a pre-existing capture pair, the maintainer assembler accepts both
`--capture-directory /trusted/pair` and `--capture-evidence /trusted/source.json`.
The evidence must be independently retained installation SourceIdentity, never an
archive-selected manifest. Only worker version/release status enter `LegacyCapture`;
actual historical files are hashed under the fixed optional `capture/` inventory.
The application digest/revision/version and exact migration/resource facts are still
verified against local image bytes. No installation identifier or Quartz snapshot is
copied into release metadata. A legacy image lacking the #701 property is supported
only through the explicitly pinned accepted Quartz SQL resource; unknown resources fail.

Target export selects the historical pair when present; append `current` to select
the newly bundled pair explicitly. An adopted installation selects the profile that
matches its independently persisted capture policy. Adoption never refreshes that
policy. Prepare the complete desired inventory before import: an occupied release
name is never repaired or expanded in place. Recovery helpers are mode 0555/0444 to
satisfy the existing immutable capture-payload contract.


## Public bootstrap and ordinary updates

The public stable artifact is `wayfarer-vX.Y.Z-linux-amd64.tar.gz`, with a matching
`.tar.gz.sha256` sidecar on that exact GitHub Release. Linux AMD64, Docker Engine/
Compose v2 and root are required. The host needs no clone, .NET SDK, Node/npm or Python.
No host package or Docker daemon configuration is installed by this distribution.

For the **next genuine stable containing this capability**, choose its exact version
and obtain the archive/sidecar through HTTPS. The following trusted-release bootstrap
uses ordinary Linux download/checksum/archive utilities. First compare the archive
SHA-256 with the Release asset's published REST `digest` (`sha256:<64 lowercase hex>`)
from `https://api.github.com/repos/stef-k/Wayfarer/releases/tags/vX.Y.Z`; the sidecar
is a convenience integrity check, not publisher authentication. Do not run downloaded
payloads before this provenance/integrity verification.

```sh
# Replace X.Y.Z with the genuine Compose stable release; no mutable latest asset exists.
release=vX.Y.Z
asset=wayfarer-$release-linux-amd64.tar.gz
sudo install -d -m 700 /opt/wayfarer-bootstrap
cd /opt/wayfarer-bootstrap
sudo curl --fail --location --proto '=https' --proto-redir '=https' --max-time 600 \
  --output "$asset" "https://github.com/stef-k/Wayfarer/releases/download/$release/$asset"
sudo curl --fail --location --proto '=https' --proto-redir '=https' --max-time 60 \
  --output "$asset.sha256" "https://github.com/stef-k/Wayfarer/releases/download/$release/$asset.sha256"
# Compare this hash to the published REST asset digest, then check the sidecar.
sudo sha256sum "$asset"
sudo sha256sum --check "$asset.sha256"
# Only extract the verified trusted publisher archive into a fresh root-owned directory.
sudo install -d -m 700 /opt/wayfarer-bootstrap/bundle
sudo tar --extract --gzip --file "$asset" --directory /opt/wayfarer-bootstrap/bundle --no-same-owner
sudo /opt/wayfarer-bootstrap/bundle/wayfarerctl release inspect /opt/wayfarer-bootstrap/bundle
# Uses the product anonymous bounded path, exact image pulls and normal immutable import.
sudo /opt/wayfarer-bootstrap/bundle/wayfarerctl release acquire "${release#v}"
sudo /opt/wayfarer-bootstrap/bundle/wayfarerctl setup --bundle "/etc/wayfarer/releases/$release"
```

Setup prompts for the remaining trusted configuration/admin password, including
`Images.ApplicationDigest` from the validated retained `release.json`. Keep this
bootstrap fixed; retained-release `dispatch` selects exact installed operators after
transitions. It does not update its own executable. No curl-pipe-shell is used.

`release acquire X.Y.Z|latest` only prefetches validated retained bytes/images and
reports path/fingerprint/version. `update --plan` uses latest stable; `update X.Y.Z
--plan` uses the exact release. Both feed the unchanged #704 plan/receipt lifecycle;
`update --accept-plan HASH` remains destructive authorization. Current/older targets,
missing assets or missing exact source compatibility fail without fallback search.
Offline `release import PATH` and `update --bundle PATH --plan` remain available.

The first public Compose stable may have `Sources=[]`: fresh setup/restore only,
with no invented update from source-only v1.9.19 or older. Subsequent publication binds
one exact compatible prior public Compose bundle or fails. The issue remains open
until genuine stable publication and fresh anonymous acquisition/setup/doctor pass;
PR candidate migration proof does not claim stable-to-stable migration.
