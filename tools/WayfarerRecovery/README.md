# Compose recovery engine

`wayfarer-recovery` is a bundle-owned self-contained Linux AMD64 executable.
The administrator-facing owner is [wayfarerctl](../../docs/self-hosting/wayfarerctl.md).
Do not invoke the private worker protocol as an alternative configuration authority.

Manual, scheduled and host-reserved quiesced captures share `RecoveryEngine`.
`installation.json` owns policy; immutable generation files are derived inputs.
The worker runs as UID/GID1654 inside the exact configured PG18.6/PostGIS DB image,
with no Docker socket, no capabilities and private bounded temp. Capture receives
app-data read-only and backend DB access; offline listing/verification receives neither
source nor credentials nor network and binds the destination read-only. The stable
lock inode lives in a root-owned non-writable parent; mutable scheduler receipts live
in a separate state directory. Exact-slot crash reconciliation reapplies retention,
and capture reclaims only bounded, day-old, validated owned publication residue.
The additive `WayfarerRecoverySource.dll` inspects the selected immutable app's real
storage, Data Protection, readiness and schema owners without changing that image.

Archive v1 has exactly five top-level USTAR members:

```text
manifest.json
database.dump
data-protection.tar.gz
uploads.tar.gz
SHA256SUMS
```

The manifest binds installation/archive UUIDs, UTC intervals, online/quiesced mode,
optional scheduled slot, app version/source SHA/image, DB image/version/extensions/
locale/migration history, stable Data Protection name, worker/payload/bundle identity
and component sizes/SHA-256. No credentials, key identifiers or uploaded filenames
are public metadata. The adjacent final archive SHA-256 sidecar commits the pair.
Integrity is independent of restore compatibility. Checksums are not signatures.

Run focused product tests:

```sh
dotnet test tests/Wayfarer.Tests --filter 'FullyQualifiedName~WayfarerRecoveryTests|FullyQualifiedName~WayfarerCtlTests'
```

Maintained disposable qualification is `tools/compose/qualify_recovery.py`, extending
the existing operator fixture with actual published binaries and a second clean
Compose reconstruction. `recovery-probe` and `lock-probe` are qualification-only
programs and are not part of the shipped payload. Their root mount/volume setup is
limited to fixture-owned paths/resources; the recovery worker remains unprivileged.

See the operator documentation for configuration, destination security, no-fallback
mount propagation, retained interruption receipts, schedule policy, bounded listing,
cancellation and deliberately stopped quiesced state. Native capture, destructive
managed restore reuses this format/verifier under the operator lifecycle. Update, release resolution and ARM remain separate contracts.
