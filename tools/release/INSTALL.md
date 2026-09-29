# Offline release bundle v1

This directory is a candidate qualification artifact unless `release.json` explicitly
records stable status. Checksums prove integrity, not publisher authenticity. Obtain
these bytes from a trusted administrator/maintainer. No public stable asset availability
is implied. The target host needs Linux AMD64, Docker/Compose and root, not an SDK.

Use a previously trusted operator to run `release inspect /absolute/bundle` and
`release verify-images /absolute/bundle`. Missing local images mean the bundle is not
execution-ready. These commands never pull images. Offline import currently accepts
an already-extracted trusted directory only; tarball import is not implemented. The
archive's SHA-256 is in the external `SHA256SUMS`, never in its own manifest.

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
Network acquisition, stable publication and stable release-to-release qualification
remain separate. See the repository operator documentation for phase and recovery details.

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
