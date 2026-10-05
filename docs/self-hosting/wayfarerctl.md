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

## version

```sh
wayfarerctl version
```

Reports the compiled CLI version. The deployed Wayfarer release identity is reported
separately by `status`.

## Dispatch and retained operators

Keep a stable verified bootstrap at a fixed root-owned path. After setup and
updates, use:

```sh
wayfarerctl dispatch COMMAND
```

`dispatch` selects the retained operator that owns the active installation.
Restore/update resume and abort select the exact retained operator recorded by
their operation receipt.
Uninstall replay selects its receipt owner, or the retained owner in the terminal
purge tombstone after installation configuration has been erased.

The bootstrap is not replaced in place by lifecycle commands.
An older bootstrap may reject uninstall grammar before dispatch. Use the explicit
bootstrap replacement or retained-operator remedy in
[the bundle instructions](../../tools/release/INSTALL.md).

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

## uninstall

Planning and acceptance are separate commands:

```sh
wayfarerctl uninstall --plan --backup
wayfarerctl uninstall --plan --without-backup
wayfarerctl uninstall --purge --plan --backup
wayfarerctl uninstall --purge --plan --without-backup
wayfarerctl uninstall --accept-plan SHA256
```

The protected plan and exact printed lowercase SHA-256 are the sole execution
authority. Review the normal/purge mode, backup/waiver decision, installation,
storage and retained operator identity, and every exact resource action. Images
and any configured backup destination appear as exclusions. No password or secret
content is printed. Changed protected authority or replaced resources invalidates
acceptance before destructive intent.

Acceptance carries only `--accept-plan SHA256`; do not repeat `--purge`, `--backup`
or `--without-backup`. There is no `--yes`, `--force` or implicit purge.

### Interactive and redirected planning

On an interactive terminal, `uninstall --plan` and `uninstall --purge --plan` may
omit the backup choice. An enabled usable policy prompts for a fresh verified
quiesced backup, defaulting to yes; answering no selects an explicit waiver. With
no usable enabled policy, type `WITHOUT BACKUP` to plan the waiver, or cancel.
Planning never creates or enables backup configuration.

Redirected/non-interactive planning must supply exactly one of `--backup` and
`--without-backup`. They are mutually exclusive.

### Final backup and waiver

`--backup` requires the existing enabled policy. Acceptance invokes ordinary
quiesced capture, obtains the exact new committed archive, and explicitly verifies
its integrity and compatibility before saving destructive uninstall intent. The
receipt binds the archive UUID, basename, SHA-256 and successful verification.
An older-set retention failure does not invalidate a successfully published and
verified final set; ordinary backup retains its own retention-failure exit status.

Capture or verification failure starts no uninstall deletion. Quiesced capture may
leave the application stopped; resolve ordinary backup recovery before `start`.
After destructive intent exists, replay uses the recorded final set without
recapturing. `--without-backup` is a hash-bound waiver of a **new** recovery set;
it does not remove old backups.

### Normal uninstall and Preserved reactivation

Normal uninstall removes only exact owned runtime containers/networks and proven
terminal helpers. It deletes **zero Docker volumes**, including cache/log/Caddy
and retained old/candidate generations. The entire protected installation root,
secrets, release authority, lifecycle metadata and backup policy remain.

`status` reports intentional **Preserved** state. `doctor` validates protected
authority and exact retained volumes without requiring running services.

`start` is the deliberate reactivation command. It proves retained storage before
Compose creation, uses the exact retained release with `--pull never`, restores an
enabled backup scheduler and requires ordinary health/doctor before archiving the
resolved receipt. It does not update or migrate. A missing or replaced volume
refuses reactivation; partial reactivation keeps its receipt for a later `start`.

While Preserved, runtime/data mutations refuse. `stop` succeeds as a no-op;
`restart` directs you to `start`. Fresh setup refuses overwrite and directs you to
reactivation or an explicit purge. A new normal plan requires successful
reactivation first.

### Purge and Purged diagnosis

Purge may start from a completed active installation or from Preserved. Planning
from Preserved requires `--without-backup`; `--backup` requires `start` first.
Acceptance archives the exact terminal normal receipt before transferring current
ownership to the accepted purge receipt.

Purge deletes only provably owned local volumes: active DB/application data,
cache/log/Caddy state, and old/candidate storage generations corroborated by valid
protected history. It then safely erases installation configuration, secrets,
backup policy/control, generations, plans and histories under the deployment root.
Foreign or ambiguous resources, unsafe filesystem entries, links and mount
crossings refuse cleanup and retain forward-recovery authority.

The configured administrator-owned backup destination, marker and archives remain
unchanged. Shared Docker images are never removed or pruned. Immutable release
cache remains independently validated.

Terminal **Purged** state retains only `releases/`, `operation.lock` and the minimal
non-secret `uninstall-purged.json` tombstone (including any valid internal release
staging evidence). `status` recognizes Purged; `doctor` checks the tombstone,
retained release/operator bytes and terminal root shape without Docker access or
service creation.

### Replay and setup after purge

Replay `uninstall --accept-plan SHA256` with the same accepted hash after an
interruption. There is no rollback after destructive intent. Exact absence is
reconciled forward; replacement or foreign ownership causes refusal. A different
plan cannot supersede unresolved uninstall authority.

Same-hash terminal replay succeeds without a new backup or Docker mutation.
Purged replay uses only its tombstone and retained operator, without deleted
installation/secrets/plan files. A different hash is refused.

Ordinary fresh `setup` on the same root consumes a valid terminal tombstone under
the host lock. It creates new secrets and setup authority, initially using the
supported no-backup `Installation == Guid.Empty` identity. It inherits no backup
policy or destination and never relabels the old installation's marker/archives.
Unresolved purge or contradictory terminal residue prevents setup; preserve the
evidence and reconcile the accepted uninstall first.

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

- native/systemd migration is not implemented;
- arbitrary Docker contexts/rootless Docker are unsupported;
- `start`/`restart` never update, pull or migrate;
- do not manually delete volumes or lifecycle receipts to make a command succeed;
- setup/update/restore fail closed on contradictory ownership/identity;
- restore requires a controlled-custody acknowledgement and exact plan hash;
- update and uninstall require an exact plan hash;
- unresolved restore/update/uninstall intent blocks ordinary mutations;
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
