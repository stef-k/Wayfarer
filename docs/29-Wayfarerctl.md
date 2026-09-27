# Operate Wayfarer with wayfarerctl

`wayfarerctl` is the Linux AMD64, self-contained C# operator executable introduced
by #648. It orchestrates the [accepted Compose substrate](28-Production-Compose.md)
and existing application maintenance commands. This foundation includes fresh setup,
lifecycle, diagnosis, logs, user recovery, opt-in Compose recovery sets and managed
restore to an independently trusted exact local target. **Update, uninstall and native migration are not implemented.** #603 is not complete; the
final versioned release tarball and `release.json` remain separate work.

## Placement and prerequisites

Use Linux AMD64 Docker Engine with the **local** `/var/run/docker.sock`, Compose v2
2.24.4 or newer, and a filesystem supporting Unix ownership/modes. Run management
as root: setup must create distinct secret files owned by root, UID999 and UID1654.
Remote Docker contexts, Docker Desktop, rootless daemons and arbitrary host bind
mounts are not supported. No host .NET runtime/SDK, Python, Node/npm or PostgreSQL
installation is needed to run the executable. Docker socket access is administrative.

Obtain the executable and complete trusted Compose source/bundle together. Pending
final release packaging, a maintainer can publish the executable from source:

```sh
dotnet publish tools/WayfarerCtl/WayfarerCtl.csproj -c Release \
  -r linux-x64 --self-contained true -o /absolute/published-ctl
```

This is a maintainer build instruction, not a host runtime prerequisite. Place the
executable on the administrator PATH, for example `/usr/local/bin/wayfarerctl`.
Place `deploy/compose/` contents intact under an immutable root-owned bundle directory:

```text
/etc/wayfarer/                       root:root 0700
  releases/vX.Y.Z/                   trusted extracted bundle, no writable shared ancestors
    compose.yaml
    external.yaml
    caddy/Caddyfile
    config/deployment.env.example
    db/20-wayfarer.sh
  installation.json                 generated non-secret discovery/config identity, 0600
  deployment.env                    generated non-secret literal Compose inputs, 0600
  secrets/                          generated, root:root 0700
  operation.lock                    per-installation serialization, 0600
  setup-progress.json               protected original-input receipt and maintenance checkpoints
  setup-complete                    created only after successful setup diagnostics
```

The default discovery root is always `/etc/wayfarer`, independent of current directory
or executable location. The global prefix `--deployment-root /absolute/path` selects
another installation. Every operation uses the persisted absolute bundle path and
project name; never move/rename these to attempt an update. The default Compose
project is `wayfarer`; `setup --project NAME` persists a distinct initial project.

Do not edit `deployment.env` independently: it must match `installation.json`.
Both are inspectable non-secret files; discrepancy fails closed. Treat deliberate
configuration repair as advanced maintenance with writers stopped. A changed image
is an update, which this CLI does not implement. No implicit image pull or migration
occurs during start/restart. Bundle/config parents must be root-owned and not writable
by others; symlink paths and unsafe secret files are refused.

## Choose an ingress mode

Managed mode uses Caddy's normal automatic public HTTPS. Arrange public DNS for the
hostname and allow 80/TCP, 443/TCP and 443/UDP; remove conflicting listeners yourself.
Only Caddy publishes ingress; PostgreSQL remains private. Setup must validate public
HTTPS `/health/live` and `/health/ready`, with normal certificate validation, before
reporting completion. Incorrect DNS, blocked ACME or a stale competing endpoint is
not successful setup. CLI checks are bounded and do not install/modify DNS or firewall.

External mode runs no Caddy service and publishes only `127.0.0.1:PORT` (default8080).
The existing host-native proxy must forward to this endpoint, preserve the public Host
and replace untrusted forwarded headers with authoritative scheme/host/client identity.
Wayfarer trusts the selected edge gateway `<edge-prefix>.1` for this qualified topology.
Loopback readiness is verified; public HTTPS and proxy forwarding remain explicitly
administrator-owned. This is suitable for an existing ingress, **not native migration**.

Both modes use an unused private `172.16-31.N.0/24`; the default prefix is `172.30.64`.
If preflight detects a Docker/host/VPN route overlap, rerun with `--edge-prefix` naming
a free prefix. Setup does not alter other networks, routes, listeners or installations.

## Fresh guided setup

From an interactive root terminal:

```sh
wayfarerctl setup --bundle /etc/wayfarer/releases/vX.Y.Z
```

Choose managed/external mode, public DNS hostname and the genuine application's
`sha256:` digest from trusted release evidence. External mode also asks for its
loopback port. The database defaults to the accepted published derived DB digest
`sha256:bd9b3bbfe1e879b56b0742646c18d0dcc9ec95180095f8f6d02e03b54feeeb61`.
Never substitute a base-image digest, local image ID or mutable tag as release evidence.

Preflight checks the host, daemon/Compose, bundle/config, paths, existing project state,
network overlap and listeners, then prints a non-secret plan. The administrator
password is entered **hidden and confirmed**. The application owns password policy;
choose a strong unique password. Setup creates the protected account named `admin`.
There is no accepted default password and no stored administrator password file.

Execution is DB healthy → fixed volume-root ownership → application `database migrate`
→ `database seed` → protected `admin bootstrap admin --stdin` → Wayfarer ready →
managed Caddy/public HTTPS or external loopback verification → status/doctor evidence.
Ordinary web startup does not migrate, seed or bootstrap.

For automation, supply every required choice and intentionally redirect one password
line from a root-owned0600 file. Placeholder values below are deliberately nonfunctional:

```sh
wayfarerctl --deployment-root /etc/wayfarer setup \
  --bundle /etc/wayfarer/releases/vX.Y.Z \
  --hostname maps.your-domain.tld --app-digest sha256:RELEASE_DIGEST \
  --mode external --loopback-port 8080 --edge-prefix 172.30.64 \
  --project wayfarer --password-stdin < /root/wayfarer-admin-input
```

Create that input through a protected editor/secret provisioning method, not a shell
command containing the password. Remove the temporary input when no longer needed.
Passwords never belong in argv, environment settings, YAML, examples or command logs.

## Secrets and durable state

Setup generates two independent cryptographic credentials, each with 32 bytes entropy.
The DB bootstrap secret is root:root0600. The same application-role credential is
copied separately to `db-app-password` (999:9990600) and `app-password` (1654:16540600).
Local Compose secrets do not remap ownership. Their parent remains root:root0700;
files are created exclusively with restrictive modes before bytes are written.
The CLI refuses replacement, links, multiple hard links, incorrect owners/modes,
malformed material or mismatched consumer copies. Never chmod them world-readable.
Changing files is not database password rotation.

Authoritative named volumes are DB data and the complete application data volume
(including uploads and Data Protection keys); preserve them together. Cache/logs are
separate and are not substitutes for durable state. Caddy retains its own TLS state.
Stop is not uninstall. No ordinary command deletes volumes or runs `down -v`.

## Menus, commands and help

Bare `wayfarerctl` on an interactive terminal offers Setup, Status, Doctor, Start,
Stop, Restart, Logs, User recovery, Help and Exit. These invoke the same handlers as
direct commands. Input/output redirection prints concise help instead of waiting.
EOF exits the menu; Ctrl-C cancels work, retains state and returns failure. Check
status/doctor before retrying an interrupted operation.

| Command | Options and result |
| --- | --- |
| `help`, `--help`, `-h` | Command catalogue and global discovery/security/exit contract |
| `help setup`, `setup --help` | Contextual setup options and example |
| `user --help`, `user reset-password --help` | Recovery usage and password protection |
| `version` | Compiled CLI version; deployed app identity is independently reported by status |
| `setup` | `--bundle`, `--hostname`, `--app-digest`; optional `--mode`, `--project`, `--edge-prefix`, `--loopback-port`, `--password-stdin` |
| `status` | Read-only config/root/mode, service health, app image/version, DB/PostGIS/citext, setup assessment |
| `doctor` | PASS/WARN/FAIL aggregation, nonzero when unhealthy; mounts/volumes/networks/keys/proxy included |
| `start` | Starts existing configuration; bounded readiness wait; no pull/migration/recreation |
| `stop` | Graceful 70-second stop; retains volumes |
| `restart` | Graceful stop followed by start and diagnosis |
| `logs [service]` | `wayfarer` default; `db` or managed `caddy`; `--tail 1..10000` (default100), `--follow` |
| `user find <identity>` | Exact username lookup delegated to application authority |
| `user reset-password <identity>` | Hidden confirmed password; `--password-stdin` for protected redirection |

Place `--deployment-root PATH` before the command. Every command/group supports
contextual help. Unknown commands/options return a relevant help hint, never guess.
Exit codes: **0** success; **1** operation failure, unhealthy diagnosis or cancellation;
**2** invalid usage/configuration. Errors go to stderr. Diagnostic FAIL lines are the
requested report and are accompanied by nonzero status.

## Diagnosis, logs and user recovery

`status`/`doctor` do not start services or repair state. A completion marker records
setup history, not current health. A stopped stack is unhealthy until deliberately
started. Doctor checks Docker/Compose, bundle and immutable refs, secrets, service
health, actual image/version coherence, expected named mounts/volumes/networks,
DB/extensions, application readiness, key-ring authority and proxy-mode endpoints.
Public provider availability and arbitrary external ingress are not inferred.
External mode always warns about the administrator's remaining public-proxy duty.

```sh
wayfarerctl status
wayfarerctl doctor
wayfarerctl logs db --tail 80
wayfarerctl logs wayfarer --follow
wayfarerctl user find admin
wayfarerctl user reset-password admin
```

Lookup uses the application's exact normalized username semantics, not fuzzy matching,
email guessing or direct EF queries. Not-found/failed maintenance returns1. Reset uses
the same application's protected Identity command, including its policy and failure
semantics; it never prints password/hash/security stamp. This bridge is not an Admin UI.
Logs use stable service names. Known DB credentials and lines marked as credential/key
material are withheld; treat application log messages as administrator-only operational
data. Do not deliberately log secrets in custom integrations.

## Interrupted setup and troubleshooting

A failed setup retains files and volumes. Inspect `status`, `doctor` and bounded logs,
correct the reported cause, then continue through the supported operator command:

```sh
sudo wayfarerctl --deployment-root /etc/wayfarer setup --resume
# If admin bootstrap has not started, supply its password through hidden terminal
# input or append --password-stdin with a protected redirected input file.
```

Continuation requires the original protected installation identity, generated config,
credential bytes and bundle files to match the protected setup receipt. It refuses
foreign resources, changed inputs, missing migrated DB volumes, unsafe permissions,
unreceipted/legacy partial state and already-completed installations. It cannot adopt
native data. Do not change setup choices on resume or replace DB credentials.
Completed migration, seed and bootstrap steps are skipped. DB health, volume ownership,
web/proxy convergence and live diagnostics are checked again; only successful diagnostics
create the completion marker. No volumes are deleted and no rollback is attempted.
A migration or seed whose success was not recorded may repeat through the application's
existing idempotent maintenance authority.

If bootstrap's result was lost, continuation first asks the application for the existing
admin. It never blindly bootstraps again. If lookup cannot confirm the account, inspect
DB/maintenance logs and correct the cause, then explicitly use:

```sh
sudo wayfarerctl --deployment-root /etc/wayfarer setup --resume --retry-admin
```

This retries transactional application bootstrap with protected password input; the
application refuses an existing account and never resets its password. A found account
still must pass application startup/admin-security readiness before setup can complete.
Passwords are not requested for an already-recorded bootstrap. Preserve the installation
lock; do not delete it to bypass another operation.

Failures during initial protected-file creation (before a complete setup receipt exists),
or mismatched/uncertain ownership remain fail-closed. Preserve the files and have an
administrator reconcile their provenance. The [raw Compose maintenance sequence](28-Production-Compose.md#advanced-fresh-initialization)
is an advanced emergency seam for such cases, not normal recovery for receipt-owned setup.
Never fabricate completion markers or infer that missing config means an empty database.

| Symptom | Action |
| --- | --- |
| Docker unavailable/denied | Verify local Engine service/socket and root access; remote contexts are intentionally ignored |
| Compose old/missing | Install supported Compose v2 through the host's normal administration path |
| Bundle/config rejected | Restore complete trusted files; reconcile installation/env identity, ownership and links |
| Network conflict | Before fresh setup choose a free private `--edge-prefix`; inspect VPN/host routes |
| Port conflict | Free managed80/443 deliberately or use external mode with a free loopback port |
| DB unhealthy | Inspect `logs db`; preserve cluster/secrets; do not delete volumes or change PG major |
| Migrate/seed/bootstrap failed | Inspect logs, correct the cause and use `setup --resume`; uncertain bootstrap requires explicit `--retry-admin` |
| Caddy/DNS/certificate failure | Check hostname/DNS, firewall80/443, `logs caddy`; retain Caddy TLS volumes |
| App readiness failure | Inspect app logs, database credentials/schema/admin bootstrap and durable/key mounts |
| External loopback works, public URL fails | Correct the external proxy's TLS, Host/forwarding and actual trusted hop |

## Qualification boundary

Focused tests exercise parsing/help/menu/EOF, validation, network overlap, explicit
Compose selection and setup ordering/failure containment/password secrecy. Publish
proof runs the self-contained executable in a plain Ubuntu host image without .NET,
Python or Node. `tools/compose/qualify_ctl.py --executable PATH --app-digest DIGEST`
performs a disposable real external setup, generated secret ownership, migrated/seeded
DB and protected admin, status/doctor, validated local TLS via a separate test Caddy,
restart with authentication/key/upload persistence and user password recovery.
CI uses the actual local image-store digest from the existing application-image dry run;
this is local candidate evidence, not a published registry/release manifest. The first
genuine application publication remains the #642 release acceptance gate.
It removes only its random labelled resources. Test-only TLS never changes production
Caddy automatic HTTPS. This is not public-CA issuance, production/native qualification,
backup/restore/update acceptance or completion of #603.

## Compose recovery sets

Explicit opt-in on a **completed** installation introduces installation schema 2
and a stable installation UUID. Existing schema 1 remains readable and backup-disabled;
status never upgrades it. Older operators reject schema 2. Interrupted setup must
be completed using its original configuration and setup receipt first.

The trusted additive payload contains the self-contained `wayfarer-recovery` and
`WayfarerRecoverySource.dll` beside it. The latter executes using the selected
immutable application's existing runtime and authority owners. It does not replace
that image or change the original bundle. Publish the worker for Linux x64 and build
`tools/WayfarerRecoverySource`; copy only the inspector DLL into the payload. Keep
both root-owned, without writable shared ancestors, and make the worker executable
but not writable (0555; inspector 0444). The operator records their combined checksum.

```sh
wayfarerctl backup configure --destination /srv/wayfarer-backups \
  --payload /etc/wayfarer/releases/recovery-v1/wayfarer-recovery
wayfarerctl backup
wayfarerctl backups
wayfarerctl verify-backup
wayfarerctl verify-backup <owned-archive-basename>
wayfarerctl backup configure --disable
```

The destination must already exist, be dedicated and empty for first configuration,
and be outside installation, bundle, secrets, Docker and application state. Only
that directory and its installation marker are provisioned; unrelated contents are
never recursively chmod/chowned. UID/GID1654 owns the destination at 0700; archive
files are 0600. No directory is created when a configured destination disappears.
Protect archives as sensitive credentials/personal data. Checksums detect corruption;
they do not encrypt, sign or authenticate a compromised destination.

Defaults: daily 03:00 UTC, stable installation jitter of 0–15 minutes, seven complete
sets, at most three attempts per daily slot, five-minute retry spacing, and one
catch-up slot. Configure `--time HH:mm` and `--retention 1..100` explicitly as needed.
Policy lives only in `installation.json`. Generated inputs are compared byte-for-byte
before use. Previous trusted installation bytes and a transition receipt support
`backup configure --recover` after interruption. An uncertain running worker blocks
recovery rather than being declared cancelled. Inspect the named owned container and
Docker daemon first; never delete `recovery.lock` to clear a busy operation.

For an administrator-mounted filesystem, use `--kind mounted` and a dedicated
root-owned 0755 propagating parent with exactly one child named `slot`, for example
`/srv/wayfarer-remote/slot`. The administrator mounts storage and establishes host
shared propagation; Wayfarer does not manage NAS credentials or mount filesystems.
Only that parent is bound with one-way `rslave` propagation. Device/inode identity,
mount separation and installation marker must match. Unmount or substitution fails
without writing to the local underlay. The daemon must support Linux propagation.

The socket-free scheduler runs in the exact configured DB image as UID/GID1654 with
no capabilities, read-only root, app-data read-only, app-role secret read-only,
bounded CPU/memory/processes and 2 GiB private temporary storage. Its operational
receipt is in local `recovery-control/state`, outside the destination. Restart/replacement
does not reset slots. Manual and scheduled captures share the same C# engine and
kernel byte-range lock; manual busy returns 1. Stop/restart/configuration respect
that lock, and deliberate stop also stops the scheduler. No host .NET, PostgreSQL,
Python, cron or systemd timer is needed. Status/doctor report policy, destination,
payload, scheduler and bounded receipt facts. The root-owned 0755 control parent
protects the root:1654 0660 lock inode and root:1654 0640 host reservation from worker
replacement. Only the scheduler state directory is UID1654-writable; worker mounts
keep the control parent read-only and grant write access to the existing lock inode.
Existing unsafe control ownership fails closed; it is never repaired by replacing a
potentially held lock.

`backups` and `verify-backup` use a separate network-free service with no application
password or app-data mount and a read-only destination. Only the existing lock inode
and private temporary storage are writable. Destination capability checks use another
network-free service with destination write access but no control, DB secret or source
mount. Capture/scheduling alone receive backend DB access and source authority.

After interruption, scheduler reconciliation accepts only the exact UTC slot. It
verifies the committed archive and reapplies retention before recording success;
retention failure remains visible as `retention-failed` without invalidating the
archive or recapturing the slot, including after the final capture attempt.

Online capture pairs a coherent exported PostgreSQL snapshot with Uploads and the
complete resolved active ring captured over time. It is **not an atomic
cross-component snapshot**. Observed source changes fail capture. Uploads includes
committed `uploads/imports`; caches, logs, thumbnails, browser/temp, raw DB files,
Caddy/TLS and host secrets/configuration are excluded. Missing Uploads is an error;
an existing empty root is valid. No source ownership or contents are changed.

`backup --quiesced` acquires lifecycle exclusion, stops the scheduler and application,
confirms no other running app-data consumer, then reserves the operation for the
same worker engine while DB remains running. The host receipt, not a worker flag,
authorizes the quiesced label. The application remains stopped afterward. Use
`wayfarerctl start` only when the protected transition is complete. Managed restore uses the same capture engine; update/native migration remain separate work.

The archive is an uncompressed USTAR with exactly `manifest.json`, `database.dump`,
`data-protection.tar.gz`, `uploads.tar.gz` and `SHA256SUMS`. The final `.sha256`
sidecar is published last and is the complete-pair marker. Retention verifies owned
complete pairs before deleting oldest excess sets. Before capture, bounded cleanup
under the shared lock reclaims private UID/GID1654 mode 0600 partials and orphan
archive/sidecar files older than 24 hours in this installation's exact generated
namespace. Final orphans also require matching manifest/sidecar identity. Fresh,
foreign, linked, invalid and wrong-owner files, and complete pairs, are preserved;
listing/verification never clean or rewrite destination content. A retention failure
reports the successfully published archive separately.
Listing scans at most 4096 destination entries and returns at most 20 newest pairs;
it is explicitly not full verification. Verification bounds bytes, entries, paths,
manifest and elapsed time, never executes SQL, and distinguishes integrity from
source/schema compatibility. Unknown compatibility is not restore readiness.

## Managed restore

`restore` restores a matched database, complete Data Protection ring and durable
Uploads (including imports) into fresh volumes. It never reloads the active DB,
merges key rings, migrates EF, seeds reference data or bootstraps an administrator.
Checksums prove integrity, not authenticity. Execute only archives from a known,
controlled custody chain; there is no untrusted archive import mode.

Use a trusted local application bundle, immutable application and DB images already
loaded into Docker, trusted capture evidence and the current restore payload. Restore
never pulls images or selects a release from manifest strings. The exact contract
includes Linux AMD64, application digest/version/revision, bundle fingerprint,
PG17/PostGIS3.6.4/citext1.6, UTF8/C.UTF-8 libc locale, ordered EF migrations, Quartz
structure and stable Data Protection identity `Wayfarer`. The historical capture
payload fingerprint is independent of the restore payload fingerprint.

### Select, plan and authorize

```bash
wayfarerctl restore --restore-payload /opt/recovery/wayfarer-recovery --plan
wayfarerctl restore <owned-archive-basename> --restore-payload /opt/recovery/wayfarer-recovery --plan
wayfarerctl restore --accept-plan <printed-sha256> --trust-controlled-backup
```

The configured recovery payload is the default restore payload; specify
`--restore-payload` when using a separate trusted restore release. Without a basename,
the newest structurally complete owned pair is selected by
completion time and name. A failed full verification never falls back to an older
pair. Basenames must match the generated v1 format and select only the configured
destination. External pairs require a literal absolute path, the adjacent exact-name
`.sha256` sidecar and `--source-installation <manifest-source-uuid>`. This acknowledges
provenance; it does not authenticate the source. An external source UUID/project may
differ from the local installation while the exact release contract must still match.

Planning copies the selected pair using no-follow/single-link reads into private,
disk-backed `restore-plans/<operation>` storage and verifies those frozen bytes with
an unprivileged, network-free helper. Source replacement cannot retarget execution.
The plan binds archive hash/UUID/source, target UUID/configuration, old/candidate
storage generations, payload identities, emergency policy and the writer cutoff.
Execution revalidates retained bytes and local authority under host serialization.
Redirected input is never approval; use the exact printed plan hash. Interactive
execution requires an explicit controlled-custody acknowledgement (or
`--trust-controlled-backup`) followed by typing exactly
`RESTORE <target-installation-uuid> <archive-uuid>`. EOF/decline leaves authority unchanged.
There is no `--yes` or `--skip-lock`.

An online archive is not a transactional snapshot across the DB and filesystem.
Plan conservatively around its capture interval and the later writes being discarded.
Staging uses local persistent disk, not the backup worker's 2 GiB tmpfs. Archive and
nested component byte bounds remain 100 GiB, with 100,000 nested entries; allow space
for the frozen pair, verified components, extracted verification ring and fresh
DB/filesystem volumes. Failed attempts retain evidence and consume additional space.
Do not delete operation storage while a restore remains unresolved.

### Emergency recovery set and fencing

An existing installation gets a fresh verified quiesced emergency set after app,
Quartz, scheduler and proxy fencing. The existing #533 engine owns capture,
verification, publication and retention. The current capture payload must understand
restore holds. Its archive is permanently held outside ordinary retention and has no
scheduled slot. A stale online backup is not a substitute.

For a genuinely broken source or unavailable destination/capacity, include
`--without-emergency-backup` when planning. The waiver is bound into the authorization
hash and removes that recovery protection. It cannot be added during execution of a
different plan. A proven clean new installation needs no emergency capture.

Actual owned restart policies are recorded, then disabled before stopping writers.
The candidate database runs on an isolated internal network, with no published port,
old DB mount, Docker socket or production network. Root initialization sees only
empty candidate volume roots. Extraction runs as UID1654, rejects links/traversal and
unsupported entries, preserves empty directories, and normalizes dirs/files to
0700/0600. Candidate SQL uses only local bootstrap authority inside that isolation.
Offline product validation checks schema, database facts, secure admin/reference
state and active protected credentials with key generation disabled and no provider
egress. An empty credential inventory is reported as “none present.”

### Clean-host disaster recovery

Start from a clean deployment root; do not partially run setup first:

```bash
wayfarerctl --deployment-root /etc/wayfarer restore --new-install \
  --archive /protected/wayfarer-recovery-v1_<source>_<time>_<archive>.tar \
  --source-installation <source-uuid> --bundle /opt/wayfarer/bundle \
  --hostname maps.example.org --project wayfarer --mode external \
  --edge-prefix 172.30.64 --loopback-port 8080 \
  --app-digest sha256:<trusted-app-digest> --db-digest sha256:<trusted-db-digest> \
  --capture-payload /opt/capture/wayfarer-recovery \
  --restore-payload /opt/restore/wayfarer-recovery \
  --target-evidence /protected/source.json --plan
wayfarerctl --deployment-root /etc/wayfarer restore \
  --accept-plan <printed-sha256> --trust-controlled-backup
```

`source.json` is the independently trusted `SourceIdentity` evidence retained from
capture preparation, not an archive-extracted manifest. The capture executable and
its adjacent `WayfarerRecoverySource.dll` must match that evidence. The restore
executable has its own adjacent trusted inspection assembly. Host/platform, Docker,
Compose, bundle, hostname, project, network and port validation reuse setup owners.
A new target UUID and local secrets are generated. Restored users/password hashes,
security stamps, settings, Quartz data, provider profiles, ring and Uploads remain
archive authority. Completion uses `restore-complete`, never fabricated setup stages.
Backup begins unconfigured. Managed Caddy obtains certificates normally; source TLS,
NAS credentials, proxy configuration, caches and logs are not restored.

### Activation, recovery and retained evidence

Schema 3 stores one generated active storage identity for DB, app-data and fresh
rebuildable cache. Schemas 1/2 remain readable and resolve canonical names. Immutable
storage overlays preserve the original bundle and route lifecycle, diagnostics,
source inspection and backup to the same generation. Old operators reject schema 3.
One atomic installation pointer commits all three roles. Local hostname/project,
proxy choices, secrets, backup destination/policy and scheduler receipts are retained.

The protected receipt progresses through authorized, fenced, emergency verified or
waived, staging, candidate validated, activation intent, activated stopped, writes
possible and accepted. Intent is flushed before irreversible boundaries. External
loopback exposure stays removed until private readiness succeeds; managed Caddy starts
after application postflight. Scheduler restart follows successful postflight.

```bash
wayfarerctl status
wayfarerctl doctor
wayfarerctl restore --resume <operation-uuid>
wayfarerctl restore --abort <operation-uuid>
```

Ordinary mutation, including `backup configure --recover`, refuses unresolved restore
intent. Resume rechecks frozen bytes and resource authority; failed staging uses a
fresh generated attempt, never partial SQL continuation. Abort is allowed only before
a candidate application writer could have run, and leaves services stopped. Once
writes may have occurred, retain the candidate pointer and fence the installation;
there is no automatic return to old data. Exit 1 reports the phase and write cutoff
state; exit 2 indicates usage/configuration/authorization mismatch, and exit 0 requires
accepted restore. Old DB/app-data/cache, failed candidate residue, frozen bytes and
held emergency archives are not automatically deleted. Later cleanup and recovery
that discards candidate writes require a separate explicit administrative decision.

## Disposable restore evidence

`tools/compose/qualify_recovery.py --restore-only` runs the published product operator
against disposable Linux AMD64 Compose installations. It checks in-place and clean-root
restore, retained volumes, Uploads/import bytes, synthetic protected provider credentials
and pre-capture production Identity tokens. It also exercises actual SQL failure and
cancellation, extraction and offline-validation failures, interrupted pointer activation,
writer acknowledgement loss, forward-only recovery, repeat restore and emergency retention.
The normal selection additionally exercises #533 capture/retention/locking and cancellation.

`python3 tools/compose/qualify_restore_daemon.py` independently proves the restart-policy
fence across a real restart of a disposable nested Docker daemon, with a positive restart
control. It requires privileged fixture containers and local Docker binaries; it has no
host Docker socket or network, and does not restart the host daemon.

No fixture evidence qualifies a real NAS, production host, M6 cutover, public stable
distribution or whole-system #603 closure.
Historical release acquisition, updates, native migration, ARM and #604 remain separate.
