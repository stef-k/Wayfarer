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

Managed update, network acquisition, stable publication and automatic activation are
not implemented. Retain current and previous bundles and their pinned local images.
