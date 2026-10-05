---
title: wayfarerctl reference
---

# wayfarerctl reference

`wayfarerctl` is the supported Linux AMD64/ARM64 operator for Wayfarer's Compose
deployment. This page is the exact operator reference. For routine workflows, start
with [Operations](operations.md).

## Global contract

Run management as root against the local Docker daemon.

The default deployment root is `/etc/wayfarer`. To select another installation,
place the global option before the command:

```sh
wayfarerctl --deployment-root /absolute/path status
```

Exit codes are:

- **0** — success;
- **1** — operation failure, cancellation or unhealthy diagnosis;
- **2** — invalid usage/configuration.

Passwords are never command-line arguments. Use hidden terminal input or protected
stdin where a command explicitly supports it.

Contextual help is available through:

```sh
wayfarerctl help
wayfarerctl help setup
wayfarerctl setup --help
```

Bare interactive invocation opens the operator menu. Redirected invocation prints
help instead of waiting for menu input.

## Dispatch and retained operators

Keep a stable verified bootstrap at a fixed root-owned path. After setup and
updates, use:

```sh
wayfarerctl dispatch COMMAND
```

`dispatch` selects the retained operator that owns the active installation.
Restore/update resume and abort select the exact retained operator recorded by
their operation receipt.

The bootstrap is not replaced in place by lifecycle commands.

If an update completes between dispatch and command startup, an old child operator
can refuse execution. Retry through the fixed bootstrap so it selects the newly
active retained operator.

## setup

Supported forms:

```sh
wayfarerctl setup
wayfarerctl setup --version X.Y.Z
wayfarerctl setup --bundle /absolute/trusted/bundle
```

Optional setup choices include:

- `--hostname DNS`;
- `--mode managed|external`;
- `--project NAME`;
- `--edge-prefix 172.30.64`;
- `--loopback-port 8080`;
- `--password-stdin`.

Bare setup acquires the latest supported stable release. `--version` selects one
exact supported stable release. `--bundle` uses a canonical trusted local bundle.
`--version` and `--bundle` are mutually exclusive.

Stable setup derives image identities from validated release metadata; image digest
override is not an ordinary operator option.

### Setup interruption

If setup reports **“Setup has not started”**, correct the cause and rerun the same
plain setup command.

If setup reports **“Setup has started”**, preserve state and use:

```sh
wayfarerctl setup --resume
```

Preserve the original `--deployment-root` option. If admin bootstrap has not
started, provide its password through the terminal or protected stdin.

`setup --resume --retry-admin` is an explicit recovery for an uncertain protected
admin bootstrap; use it only when the operator's state calls for that action.

If protected setup state cannot safely resume, do not delete it. Preserve the
deployment root and volumes for reconciliation.

## Read-only diagnosis

### status

```sh
wayfarerctl status
```

Reports deployment/config identity, service health, application image/version,
database facts and setup/recovery assessment. It does not mutate the installation.

### doctor

```sh
wayfarerctl doctor
```

Performs bounded PASS/WARN/FAIL diagnostics across configured services, images,
mounts, volumes, networks, database/extensions, Data Protection authority and the
selected ingress path. Unhealthy diagnosis returns exit 1.

Neither command starts services or repairs protected state.

## Service lifecycle

```sh
wayfarerctl start
wayfarerctl stop
wayfarerctl restart
```

- `start` starts the configured services and waits for health;
- `stop` performs a graceful stop with a 70-second budget;
- `restart` performs stop, start and health verification.

These commands preserve volumes. They do not pull images, run migrations or
change release authority.

## logs

```sh
wayfarerctl logs [wayfarer|db|caddy] [--follow] [--tail N]
```

Defaults:

- service: `wayfarer`;
- tail: 100 lines;
- allowed `N`: 1..10000.

`caddy` is valid only for managed ingress.

## User recovery

```sh
wayfarerctl user find <identity>
wayfarerctl user reset-password <identity>
wayfarerctl user reset-password <identity> --password-stdin
```

Identity lookup uses Wayfarer's exact normalized username semantics. It does not
guess by email, ID or partial match.

Password reset uses the application's Production Identity policy: at least 15
characters including uppercase, lowercase, a digit and a non-alphanumeric
character. Rejection leaves the existing credential unchanged.

## Backup configuration

Backups are opt-in and require a completed installation.

Configure:

```sh
wayfarerctl backup configure \
  --destination /srv/wayfarer-backups \
  --payload /absolute/immutable/wayfarer-recovery
```

Optional settings:

- `--kind local|mounted`;
- `--retention 1..100`;
- `--time HH:mm`.

Disable or recover an interrupted configuration transition:

```sh
wayfarerctl backup configure --disable
wayfarerctl backup configure --recover
```

The destination must already exist and be dedicated to Wayfarer. First
configuration refuses a non-empty or unsafe destination.

For `--kind mounted`, the administrator owns the mount. Wayfarer does not mount
NAS storage or manage NAS credentials. Use a dedicated root-owned mode-0755 parent
with shared mount propagation and exactly one child named `slot`, for example:

```text
/srv/wayfarer-remote/
  slot/
```

Wayfarer binds only that `slot` through the validated one-way propagation boundary.
If the mount disappears or is substituted, capture fails instead of writing into the
local underlay.

Default policy is daily at 03:00 UTC, stable 0–15 minute installation jitter and
seven retained complete sets.

## backup

```sh
wayfarerctl backup
wayfarerctl backup --quiesced
```

A recovery set captures:

- PostgreSQL dump;
- complete active Data Protection key ring;
- durable Uploads.

The normal capture is online and is not an atomic snapshot across database and
filesystem.

`--quiesced` fences/stops application writers before capture and leaves the
application stopped afterward.

Archive contents are bounded and include manifest, database dump, Data Protection
archive, Uploads archive and checksums. Complete archive/sidecar publication is the
owned recovery-pair boundary.

## backups and verify-backup

```sh
wayfarerctl backups
wayfarerctl verify-backup
wayfarerctl verify-backup <owned-archive-basename>
```

`backups` lists at most the newest 20 owned complete pairs. Listing is not full
verification.

`verify-backup` performs non-destructive integrity and compatibility checks. A
successful integrity check still does not authenticate an archive obtained from an
untrusted custody chain.

## restore

### Plan

```sh
wayfarerctl restore --restore-payload /trusted/wayfarer-recovery --plan

wayfarerctl restore <owned-archive-basename> \
  --restore-payload /trusted/wayfarer-recovery --plan
```

Without a basename, the newest structurally complete owned pair is selected. If
that selected pair fails verification, restore does not silently fall back to an
older one.

### Authorize

```sh
wayfarerctl restore \
  --accept-plan <printed-sha256> \
  --trust-controlled-backup
```

There is no generic `--yes`. The accepted hash binds the exact planned archive
and target state.

By default, restore on an existing installation requires a fresh verified quiesced
emergency recovery set before activation. If the source is genuinely broken or the
recovery destination/capacity is unavailable, `--without-emergency-backup` may be
included **when planning**. This is a destructive waiver bound into that plan hash;
it cannot be added during execution of a different plan.

Restore stages database, key ring and Uploads into fresh candidate storage, validates
them offline, then activates the candidate. It never merges key rings, silently
runs an arbitrary archive migration, or trusts archive metadata as target authority.

### Resume and abort

```sh
wayfarerctl restore --resume <operation-uuid>
wayfarerctl restore --abort <operation-uuid>
```

Abort is allowed only before a candidate application writer could have run. After
the writer cutoff, recovery is forward-only. Preserve candidate storage and the
operation receipt.

Ordinary mutations refuse unresolved restore intent.

### External archive

External recovery pairs require an absolute archive path, the adjacent exact-name
sidecar and explicit source-installation identity. This acknowledges provenance; it
does not authenticate untrusted bytes.

### Clean-host disaster recovery

A new host must begin from a clean deployment root. Do not partially run setup
first.

The new-install restore form requires explicit trusted release/bundle, target
identity, hostname/proxy/network choices, capture/restore payloads and target
evidence before plan authorization. Use:

```sh
wayfarerctl --deployment-root /etc/wayfarer restore --new-install \
  --archive /protected/wayfarer-recovery-v1_<source>_<time>_<archive>.tar \
  --source-installation <source-uuid> \
  --bundle /trusted/bundle \
  --hostname maps.example.org \
  --project wayfarer \
  --mode external \
  --edge-prefix 172.30.64 \
  --loopback-port 8080 \
  --app-digest sha256:<trusted-app-digest> \
  --db-digest sha256:<trusted-db-digest> \
  --capture-payload /trusted/capture/wayfarer-recovery \
  --restore-payload /trusted/restore/wayfarer-recovery \
  --target-evidence /protected/source.json \
  --plan
```

Then authorize the printed plan hash through the ordinary restore acceptance
command.

New-host restore generates local installation secrets. Restored application users,
password hashes, settings, Quartz data, provider profiles, Data Protection ring and
Uploads come from the archive. Public certificates, proxy configuration, caches,
logs and NAS credentials do not.

## update

Public stable planning:

```sh
wayfarerctl update --plan
wayfarerctl update X.Y.Z --plan
```

Trusted local planning:

```sh
wayfarerctl update --bundle /trusted/extracted/bundle --plan
```

Execution:

```sh
wayfarerctl update --accept-plan <printed-sha256>
```

The target must be strictly later and explicitly support the exact installed
release authority. Same-version repair, downgrade, unknown migration boundaries
and unsupported source fingerprints are refused.

Execution fences writers and requires a fresh verified quiesced recovery set before
migration. The application/database/storage transition is recorded durably before
irreversible boundaries.

Recovery:

```sh
wayfarerctl update --resume <operation-uuid>
wayfarerctl update --abort <operation-uuid>
wayfarerctl update --restore <operation-uuid>
```

Abort is permitted only before migration may have started. Once migration may have
started, an old image is not database rollback; use the managed restore path.

## Public release acquisition

```sh
wayfarerctl release acquire X.Y.Z
wayfarerctl release acquire latest
```

Acquisition downloads and verifies one public stable release and its exact images
into retained release authority. It does **not** activate it, migrate the database
or switch the current installation.

Current public acquisition rejects exact releases below the v1.9.21 Compose floor.

## Local release inspection and retention

Advanced/offline commands include:

```sh
wayfarerctl release inspect /absolute/bundle
wayfarerctl release verify-images /absolute/bundle
wayfarerctl release import /absolute/bundle
wayfarerctl release adopt /absolute/bundle
wayfarerctl release target /absolute/bundle project [current|legacy]
wayfarerctl release corroborate /absolute/bundle /absolute/source.json
wayfarerctl release reconcile .stage-ID
wayfarerctl release unpack ARCHIVE /absolute/empty-private-stage
```

These are release-authority operations, not substitutes for normal setup/update.
Root-only image/import/adoption operations require protected trusted input ancestry.
Interrupted retained-release placement is reconciled through its owned stage/receipt;
partial stages are evidence and are not selected automatically.

## Exact v1.9.21 backup-source repair

Later repair-capable operators include one narrowly bounded command for the exact
public v1.9.21 backup-source metadata defect:

```sh
sudo /absolute/root-owned/repair-capable/wayfarerctl \
  --deployment-root /etc/wayfarer \
  release repair-backup-source-v1.9.21
```

This command is **not** a general repair or metadata override. It accepts no release,
payload or policy selector and fails unless the installation matches the exact
public v1.9.21 repair authority encoded in the operator.

Invoke the accepted repair-capable executable directly; v1.9.21's immutable retained
operator does not contain the command.

If interrupted publication leaves an owned backup transition, recover that
transition with the original retained operator before rerunning the repair.

## Safety and refusal rules

The following are durable operator boundaries:

- uninstall is not implemented;
- native/systemd migration is not implemented;
- arbitrary Docker contexts/rootless Docker are unsupported;
- `start`/`restart` never update, pull or migrate;
- no command should require deleting volumes or lifecycle receipts to succeed;
- setup/update/restore fail closed on contradictory ownership/identity;
- restore requires a controlled-custody acknowledgement and exact plan hash;
- update requires an exact plan hash;
- unresolved restore/update intent blocks ordinary mutations;
- restore abort stops being available once candidate writes may have occurred;
- update abort stops being available once migration may have started;
- old images are not database rollback after migration;
- backup checksums detect corruption but do not authenticate a compromised
  destination;
- changing generated secret files manually is not credential rotation.

When the operator refuses because protected evidence is inconsistent, preserve that
evidence. Manual deletion or permission changes can destroy the information needed
for safe recovery.

## Managed and external ingress

Managed mode owns Caddy and public HTTPS. Setup requires valid public HTTPS health.

External mode owns only loopback application exposure; your existing reverse proxy
owns TLS and trusted forwarding headers.

Neither mode makes PostgreSQL public.

## Data and recovery boundary

The durable recovery set is:

1. PostgreSQL;
2. complete active Data Protection key ring;
3. durable Uploads.

Treat all three as one recovery boundary.

Caches, logs, thumbnails, Caddy certificate state and host proxy configuration are
not substitutes for that set.

[Self-hosting guide](index.md) · [Operations](operations.md) ·
[Troubleshooting](troubleshooting.md)
