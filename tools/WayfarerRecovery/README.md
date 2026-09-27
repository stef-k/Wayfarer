# Recovery worker development checkpoint

This is an **incomplete implementation of #533**, not a supported backup feature.
The existing `wayfarerctl` commands and installation schema are unchanged.
Do not configure a production installation to invoke this worker.

The current code introduces the bundle-owned self-contained C# worker, Linux
handle-relative file access and byte-range locking, initial custom-dump capture,
archive models, verification, publication and retention. These paths still need
integration and further contract validation before release.

## Completed local evidence

The starting main revision was `835627d07fee37f723ea9facd92706076fcfcad1`.
The accepted image used for disposable qualification was:

```text
ghcr.io/stef-k/wayfarer-db@sha256:bd9b3bbfe1e879b56b0742646c18d0dcc9ec95180095f8f6d02e03b54feeeb61
```

Local checks demonstrated:

- The published self-contained worker runs as UID/GID1654 with read-only root,
  no capabilities, no network, no-new-privileges, init, and bounded memory/CPU.
  Its runtime check exercises tar/gzip, SHA-256, JSON and PostgreSQL17.11 tooling.
- A disposable fresh database initialized by the shipped DB initialization script
  permits a custom-format dump by the non-superuser `wayfarer` role, followed by
  successful `pg_restore --list`. This was not a complete application database.
- Host and container C# processes using the same recovery-lock implementation
  demonstrate contention and acquisition after host-owner death, without DB access.
- An unchanged UID1654 container using a dedicated `rslave` parent observes host
  unmount and replacement-mount events. Privileged fixture setup touched only an
  isolated temporary mount; the worker itself had no privileges. This is propagation
  evidence, not complete remote-destination/no-fallback product qualification.
- A separate disposable probe using the worker's dependencies performs a Data
  Protection round-trip and captures its synthetic ring in the accepted DB image.
  This is not production credential/authentication continuity or restore evidence.
- Twelve focused tests cover binary import bytes larger than the text-runner cap,
  empty Uploads, unsafe names and symlinks, non-overwriting publication, required
  manifest fields, and corruption rejection without source mutation.

Run the focused tests with:

```bash
dotnet test tests/Wayfarer.Tests/Wayfarer.Tests.csproj \
  --filter FullyQualifiedName~WayfarerRecoveryTests
```

No architecture stop condition has been demonstrated by these checks.

## Still required before #533 acceptance

- Schema2 installation identity and explicit backup configuration, including
  interrupted-write recovery, immutable additive payload validation and derived inputs.
- Resolved application source/readiness/schema authority integration and complete
  source compatibility validation; current capture paths alone are not sufficient.
- Administrator `backup`, `backups`, `verify-backup`, status/doctor integration,
  owned-container cancellation, and complete bounded structured result handling.
- Container-native scheduler, receipts, catch-up/retry/reconciliation, and lifecycle
  coordination using the same recovery exclusion.
- Evidence-driven quiesced capture.
- Complete destination permission/mount/marker provisioning and validation, including
  unavailable/substituted/read-only/full destinations and no underlay fallback.
- Complete archive safety/compatibility, publication-crash, retention, cancellation
  and primary-error/cleanup behavior validation. Current tests are not exhaustive
  acceptance evidence for these initial implementations.
- Shipped-worker CI execution inside the accepted image and complete Compose/operator
  qualification, including manual/list/verify and scheduled/competing executions.
- Full application DB + matching ring + Uploads reconstruction into a second clean
  disposable Compose project, protected-provider and authentication continuity,
  and proof that the source application state remains unchanged.
- Operator/recovery documentation and changelog, final exact-head CI, and independent
  exact-head review.

Keep the PR draft/unmerged and #533/#603 open. Do not treat this checkpoint as
implementation completion or production recovery qualification.
