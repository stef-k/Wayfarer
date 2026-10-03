# Operate Wayfarer with wayfarerctl

`wayfarerctl` is the Linux AMD64/ARM64, self-contained C# operator executable introduced
by #648. It orchestrates the [accepted Compose substrate](28-Production-Compose.md)
and existing application maintenance commands. This foundation includes fresh setup,
lifecycle, diagnosis, logs, user recovery, opt-in Compose recovery sets and managed
restore to an independently trusted exact local target, and managed forward update.
Public stable acquisition and guided setup share the retained `release.json` authority.
**Uninstall and native migration are not implemented.** Check
[release availability](02-Install-and-Dependencies.md#availability) before public
acquisition. Maintainer release work starts at
[Versioning and Release Operations](23-Versioning.md).

## Placement and prerequisites

Use native Linux AMD64 or ARM64 Docker Engine with the **local** `/var/run/docker.sock`, Compose v2
2.24.4 or newer, and a filesystem supporting Unix ownership/modes. Run management
as root: setup must create distinct secret files owned by root, UID999 and UID1654.
Remote Docker contexts, Docker Desktop, rootless daemons and arbitrary host bind
mounts are not supported. No host .NET runtime/SDK, Python, Node/npm or PostgreSQL
installation is needed to run the executable. Docker socket access is administrative.

The primary public assets are `wayfarerctl-linux-amd64.tar.gz` and
`wayfarerctl-linux-arm64.tar.gz` on the exact official
stable GitHub Release, with a matching `.sha256` sidecar. It contains exactly one
executable named `wayfarerctl`, byte-identical to the operator in that release's
canonical deployment bundle. It is assembled from those existing bytes, never built
as a second operator. Each platform uses the same commands and lifecycle. The native
operator, host, Docker daemon and selected bundle must agree; cross-architecture
setup, update and restore fail closed. Raspberry Pi 5 with 64-bit Ubuntu Server is
an example ARM64 host, with no Pi-specific commands or deployment path.

Obtain the tarball through the official
[Release page](https://github.com/stef-k/Wayfarer/releases). Compare its SHA-256 with
the asset's REST `digest` (`sha256:<64 lowercase hex>`) at the exact tag endpoint
`https://api.github.com/repos/stef-k/Wayfarer/releases/tags/vX.Y.Z` before extraction
or execution. The sidecar is an optional human/offline integrity check and is not
publisher authentication. Extract only verified trusted bytes in a fresh directory:

```sh
sha256sum wayfarerctl-linux-amd64.tar.gz   # compare with that Release asset digest
sha256sum --check wayfarerctl-linux-amd64.tar.gz.sha256  # if the sidecar was obtained
tar -xzf wayfarerctl-linux-amd64.tar.gz
chmod +x wayfarerctl
sudo ./wayfarerctl setup
```

Choose a root-owned protected bootstrap directory for ongoing use, for example
`/usr/local/lib/wayfarer-bootstrap/`. Keep this executable fixed; installed operations
use `dispatch` below. No host package installation, daemon configuration, source clone,
SDK, Python, `unzip` or curl-pipe-shell bootstrap is involved.

The full `wayfarer-vX.Y.Z-linux-amd64.tar.gz` deployment archive remains the secondary
explicit/local distribution seam. For development/qualification use the
[candidate assembler](25-Container-Release-Contract.md#local-release-authority-v1).
A maintainer can build the self-contained operator from source:

```sh
dotnet publish tools/WayfarerCtl/WayfarerCtl.csproj -c Release \
  -r linux-x64 --self-contained true -o /absolute/published-ctl
```

Use `-r linux-arm64` on ARM64. This is a maintainer build instruction, not a host runtime prerequisite. Guided setup
retains canonical bundles automatically. The installation layout is:

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
  setup-provisioning/                complete protected initial inputs, present only during publication/recovery
  setup-provisioning-reclaim/        private duplicate cleanup after canonical input verification
  setup-complete                    created only after successful setup diagnostics
  restore-complete                  last restore finalization UUID; distinct from setup progress
  deployment-generations/           immutable release-specific generated environment inputs
  storage-generations/              generated mappings for paired active durable volumes
  recovery-generations/             generated backup worker/scheduler inputs
  recovery-control/                 protected restore/update intent, delegation and recovery exclusion
```

The [persisted lifecycle authority contract](25-Container-Release-Contract.md#persisted-compose-lifecycle-authority)
defines schemas, companion validation, commit points and recovery states. A single
`installation.json` file is not a valid or resumable installation by itself. Preserve
the complete installation folder and follow the operator's validated recovery action.

The default discovery root is always `/etc/wayfarer`, independent of current directory
or executable location. The global prefix `--deployment-root /absolute/path` selects
another installation. Every operation uses the persisted absolute bundle path and
project name; never move/rename these to attempt an update. The default Compose
project is `wayfarer`; `setup --project NAME` persists a distinct initial project.

Do not edit deployment inputs independently: they must match `installation.json`.
Initially they are in `deployment.env`; after update the active immutable file is
`deployment-generations/<release-fingerprint>/deployment.env`. The old root file is
retained evidence, not active Compose input. Use the operator to select the active file.
These are inspectable non-secret files; discrepancy fails closed. Treat deliberate
configuration repair as advanced maintenance with writers stopped. A changed image
requires the explicit managed update authorization below. No implicit image pull or migration
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
sudo ./wayfarerctl setup
```

The recommended fresh install is bare setup after extracting the lean bootstrap.
With no `--bundle`, setup resolves latest stable,
uses the existing anonymous bounded public acquisition owner to verify/download/
extract/import the matching platform bundle, and pulls/reverifies its exact images.
`setup --version X.Y.Z` selects one exact stable through that same resolver;
`--version` and `--bundle` are mutually exclusive. Missing assets and network failures
stop without fallback search, database mutation or a current-release switch.

For `wayfarerctl setup --bundle /absolute/trusted/bundle`, setup skips network discovery/download,
validates the canonical `release.json` bundle/platform, verifies already-local exact
images and imports through the same `ReleaseStore`. Both routes derive application,
accepted DB and pinned Caddy identities from validated release metadata, record
schema-4 retained release authority during fresh setup, and enter the same setup
engine. No digest copying or separate post-setup adoption is required. Existing
installations still use explicit `release adopt` where applicable.

Choose managed/external mode and a public DNS hostname. External mode also asks for
its loopback port. Image identities are never an ordinary administrator prompt.
The existing disposable candidate/raw-template qualification seam retains explicit
`--bundle PATH --app-digest sha256:HEX`; stable bundles reject that override.

Preflight checks the host, daemon/Compose, bundle/config, paths, existing project state,
network overlap and listeners, then reports that the checks passed. The administrator
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
  --version X.Y.Z --hostname maps.your-domain.tld \
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
EOF exits the menu; Ctrl-C cancels work, retains state and returns failure. During
setup, follow the reported plain-setup or resume instruction below. For an existing
installation's other operations, check status/doctor before retrying.

| Command | Options and result |
| --- | --- |
| `help`, `--help`, `-h` | Command catalogue and global discovery/security/exit contract |
| `help setup`, `setup --help` | Contextual setup options and example |
| `user --help`, `user reset-password --help` | Recovery usage and password protection |
| `version` | Compiled CLI version; deployed app identity is independently reported by status |
| `setup` | Latest public stable by default; `--version X.Y.Z` or `--bundle PATH`; `--hostname` or interactive choices, optional `--mode`, `--project`, `--edge-prefix`, `--loopback-port`, `--password-stdin` |
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

Setup reports what it is doing: checking this computer, finding and downloading
Wayfarer, verifying the download and required containers, preparing the installation,
creating the administrator, starting Wayfarer and checking readiness. The primary
failure explains the failed stage, retained state and the recommended next command.
Raw exception text, child stderr, passwords and protected configuration are withheld.

Temporary network failures are retried automatically: one attempt plus at most
three retries, normally after 2, 5 and 10 seconds. A valid server `Retry-After` is
capped at 30 seconds. Retries apply only to release discovery, the exact advertised
download and exact container pulls. An interrupted download restarts in private
staging; partial bytes never gain authority. A briefly unavailable advertised asset
may retry, but a missing release never triggers fallback search. Integrity, identity,
archive, platform, ownership and retained-release contradictions stop immediately.
Setup mutations never receive automatic retries.

**If the error says "Setup has not started", correct its reported cause and run the
same plain setup command again**, including your original options:

```sh
sudo ./wayfarerctl setup
```

No canonical installation configuration, credentials or service data has been created
by that attempt. Private staging may contain generated inputs; it is not committed
installation state. The private installation folder, `operation.lock`, verified releases and
non-authoritative preparation/receipted placement stages under `releases/` may remain.
These do not prevent plain setup; an identical retained release is revalidated and
reused. Do not delete them. `setup --resume` and `doctor` need installation state and
are not the recovery commands for this preparation boundary. For GitHub download
errors, check internet access to github.com; for container downloads, check ghcr.io
and the Caddy registry (docker.io). For an integrity/safety error, do not bypass the
check; try again later using the official release.

**If the error says "Setup has started", retain all installation files, credentials
and service data, correct the reported cause, and run `setup --resume`.** Preserve
the original `--deployment-root` option when one was used:

```sh
sudo wayfarerctl --deployment-root /etc/wayfarer setup --resume
# If admin bootstrap has not started, supply its password through hidden terminal
# input or append --password-stdin with a protected redirected input file.
```

Initial configuration, credentials and the setup receipt are prepared together in
private staging under `releases/`. A failure there leaves the plain setup command
usable. Only a complete verified snapshot becomes protected provisioning authority.
If publishing its installation files is interrupted, `setup --resume` finishes
publication from those original bytes before starting the normal setup sequence.
Existing files must match exactly; credentials are never regenerated or replaced.
After the canonical inputs and setup receipt verify, an atomic rename and installation
directory flush transfer the redundant snapshot to private reclamation. If cleanup is
interrupted, `setup --resume` validates the canonical identity, credentials and receipt
independently, then reclaims the remaining verified duplicates before setup execution.
Coexisting snapshot phases, foreign or unsafe cleanup files, changed inputs and an
incomplete authoritative snapshot require administrator reconciliation; preserve them.

Continuation requires the original protected installation identity, generated config,
credential bytes and bundle files to match the protected setup receipt. It refuses
foreign resources, changed inputs, missing migrated DB volumes, unsafe permissions,
unreceipted/legacy partial state and already-completed installations. It cannot adopt
native data. Do not change setup choices on resume or replace DB credentials.
If continuation refuses incomplete or unsafe protected state, preserve it and follow
the reported validation cause; plain setup cannot overwrite or repair it.
Completed migration, seed and bootstrap steps are skipped. DB health, volume ownership,
web/proxy convergence and live diagnostics are checked again; only successful diagnostics
create the completion marker. Checkpoints and completion flush their installation
directory before the next dependent mutation or successful completion. A durability
failure stops setup; an uncertain completion marker that cannot be removed requires
reconciliation instead of another resume. No volumes are deleted and no rollback is attempted.
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

**If the error says "cannot safely resume", preserve the installation folder and
have an administrator reconcile its protected state.** This applies to older partial
installations without a complete receipt/snapshot, changed files or uncertain ownership.
Neither plain setup nor another resume attempt repairs that state. Restore the exact
original protected inputs and their ownership from trusted retained evidence; if that
evidence is unavailable, establish provenance before choosing further recovery.
The administrator must identify the owning snapshot/receipt and validate its original
inputs and resource provenance against the
[formal state inventory](25-Container-Release-Contract.md#authority-inventory).
Do not fabricate a receipt, edit JSON to select guessed resources, discard a partial
snapshot or remove an exclusion marker as routine repair. The inventory records known
recovery gaps so an unsupported retry is not mistaken for a supported repair.
The [raw Compose maintenance sequence](28-Production-Compose.md#advanced-fresh-initialization)
remains an advanced emergency seam, not ordinary recovery for receipt-owned setup.
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
this is local candidate evidence, not a published registry/release manifest. Public
distribution requires the separate
[release acceptance gate](27-Application-Image-Publication.md#stable-compose-distribution).
It removes only its random labelled resources. Test-only TLS never changes production
Caddy automatic HTTPS. This is not public-CA issuance, production/native qualification,
backup/restore/update acceptance or completion of #603.

## Compose recovery sets

Explicit opt-in on a **completed** installation assigns an absent installation UUID
and selects at least schema 2; current retained-release installations stay schema 4.
Existing schema 1 remains readable and backup-disabled; status never upgrades it.
Older operators reject unsupported schemas. Interrupted setup must be completed using
its original configuration and setup receipt first. The
[schema/companion contract](25-Container-Release-Contract.md#installation-json-schema-and-evolution)
also covers storage generations and historical completion.

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
Recovery retains whichever exact old/next installation pointer was committed; it
does not automatically switch policy or roll back. Prepared worker files have no
independent authority. After a completed restore or resolved update, the same
`wayfarerctl backup configure --recover` command also clears exact stale worker
exclusion markers when protected receipts and completion evidence agree. Missing,
unresolved, foreign or contradictory evidence remains blocking; preserve it for
administrator reconciliation. Recovery retains accepted/aborted history and does
not repeat the restore/update to clear a marker. See the
[backup transition diagram](25-Container-Release-Contract.md#backup-configuration-commit-and-recovery).

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
`wayfarerctl start` only when the protected transition is complete. Managed restore and update use the same capture engine; native migration remains separate work.

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

### Versioned Quartz recovery identity

The schema 2/3 numbers in this section refer to recovery
`SourceIdentity.ConfigurationSchema`, independently of the local installation
schema described in the [installation contract](25-Container-Release-Contract.md#installation-json-schema-and-evolution).

New explicit `backup configure` inspections produce `Source.ConfigurationSchema=3`.
`QuartzSchemaInstaller` owns `wayfarer-quartz-postgres-v1`, the release compatibility
contract shared by fresh and supported additive-upgrade layouts. It changes only
when product restore compatibility changes. The inspector reads this authority from
the loaded application image, without web/jobs or database mutation. Older images
without that capability cannot be newly configured as schema 3; their existing
schema-2 backup policies remain usable without automatic rewriting.

Schema 3 separately records `QuartzSnapshotFingerprint`: MD5 over PostgreSQL JSON
column tuples ordered by table/column name under C collation. Facts include type,
UDT identity, nullability, length/precision, defaults, identity and generation
attributes for `qrtz_` columns in the current schema. It excludes physical ordinal
and user data. This is capture drift evidence, not an authenticity check or release
contract. Capture recomputes it inside the read-only snapshot exported to `pg_dump`;
a mismatch fails before dumping. EF migration and DB/extension checks still apply.

Legacy schema 2 retains its original `QuartzIdentity` algorithm (table/column/type/
nullability in physical ordinal order) and capture equality. Historical archives and
persisted policies are never rewritten. A schema-2 target still requires exact
legacy Quartz equality and rejects schema-3 archives. A schema-3 target compares
only the release Quartz contract for schema-3 archives, not their snapshot values.

A schema-3 target accepts schema-2 archives only when its independently trusted
`SupportedLegacySourceSchemas` explicitly includes `2`. Application version/source/
image, database image/major/extensions, full EF history, stable Data Protection
application identity, bundle/capture payload, worker version and release status must
match. Archive metadata cannot grant legacy support. This is bounded exact-release
compatibility, not permission to import arbitrary old releases. Archive integrity,
explicit trusted provenance, isolated SQL staging and offline product validation
remain mandatory; production Quartz validation is the final restored-schema owner.

The outer archive remains version 1. No EF migration or Quartz table reordering is
introduced. Retained release bundles now reconstruct this target contract; managed update uses the independently retained source and target authorities below.

## Managed restore

`restore` restores a matched database, complete Data Protection ring and durable
Uploads (including imports) into fresh volumes. It never reloads the active DB,
merges key rings, migrates EF, seeds reference data or bootstraps an administrator.
Checksums prove integrity, not authenticity. Execute only archives from a known,
controlled custody chain; there is no untrusted archive import mode.

Use a trusted local application bundle, immutable application and DB images already
loaded into Docker, trusted capture evidence and the current restore payload. Restore
never pulls images or selects a release from manifest strings. The exact contract
includes the selected Linux AMD64/ARM64 platform, application digest/version/revision, bundle fingerprint,
PG18.6 (`18.6-1.pgdg12+2`)/PostGIS3.6.4/citext1.8, UTF8/C.UTF-8 libc locale, ordered EF migrations, Quartz
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
DB/filesystem volumes. Reverification stops and reconciles the exact operation's helpers,
then reclaims superseded verification trees before extraction; plan, execution and resume
retain only one verified component tree. Frozen bytes and failed candidate volumes remain
retained. Do not delete operation storage while a restore remains unresolved.

Capacity preflight measures available deployment/Docker/destination space, which already
excludes retained old and failed generations. Before freezing it budgets the frozen pair
and component staging; nested ring extraction checks expanded writes. Before fencing and
activation it reserves candidate file bytes, cache bootstrap, emergency output/spooling and
one GiB of operating headroom. DB/index/WAL allowance is the greater of one GiB, four times
the dump, and twice the observed old DB footprint. This is an obvious-shortage gate, not a
restore-size guarantee: compressed SQL cannot predict all database/index/WAL growth.
The checks conservatively include allocations even when the filesystems are separate.
An insufficient-capacity result retains old volumes and backups; it never deletes them to
make space.

### Emergency recovery set and fencing

An existing installation gets a fresh verified quiesced emergency set after app,
Quartz, scheduler and proxy fencing. The existing #533 engine owns capture,
verification, publication and retention. The current capture payload must understand
restore holds. Its archive is permanently held outside ordinary retention and has no
scheduled slot. The hold is durably published before the archive/sidecar pair. Failed or
interrupted publication is reconciled under the recovery lock: private pending holds and
owned holds without a complete pair are reclaimed; committed held pairs remain protected.
A stale online backup is not a substitute.

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
rebuildable cache; retained-release installations preserve schema 4 with that identity.
Schemas 1/2 remain readable and resolve canonical names. Immutable
storage overlays preserve the original bundle and route lifecycle, diagnostics,
source inspection and backup to the same generation. Old operators reject schema 3.
One atomic installation pointer commits all three roles. Local hostname/project,
proxy choices, secrets, backup destination/policy and scheduler receipts are retained.

The protected `recovery-control/restore.json` receipt progresses through:

```text
Authorized -> Fenced -> EmergencyVerifiedOrWaived -> Staging
-> CandidateValidated -> ActivationIntent -> ActivatedStopped
-> WritesPossible -> Accepted

Before WritesPossible: --abort -> Aborted (services remain stopped)
Update-owned restore: no abort back into migrated old storage
```

Intent is flushed before irreversible boundaries. External
loopback exposure stays removed until private readiness succeeds; managed Caddy starts
after application postflight. Scheduler restart follows successful postflight. The receipt
stays at writes possible throughout restart-policy restoration and durable `restore-complete`
publication. Accepted is the final checkpoint and only then clears restore intent. A failure
or process death during finalization still requires forward-only resume; completion is never
inferred from an Accepted receipt when its completion marker is absent.

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
If an accepted/aborted receipt remains alongside stale worker exclusion, run
`wayfarerctl backup configure --recover`. It requires the exact operation marker
and the completion relationship appropriate to that terminal receipt before
removal. Accepted restore still requires its own completion UUID; abort cannot
claim that acceptance. Missing, foreign or contradictory evidence remains blocking
and untouched. Preserve such evidence for administrator reconciliation; never
manually delete markers. Ordinary resume keeps its resolved-operation semantics.

## Disposable restore evidence

`tools/compose/qualify_recovery.py --restore-only` runs the published product operator
against disposable Linux AMD64 Compose installations. It checks in-place and clean-root
restore, retained volumes, Uploads/import bytes, synthetic protected provider credentials
and pre-capture production Identity tokens. It also exercises actual SQL failure and
cancellation, extraction and offline-validation failures, interrupted pointer activation,
writer acknowledgement loss, forward-only recovery, repeat restore and emergency retention.
The maintained lifecycle extension also covers restart-restoration process death, completion
write failure, bounded repeated verification, a real small-filesystem capacity refusal and
emergency-worker deaths before and after publication. Both full and restore-only selections
include these lifecycle regressions.
The normal selection additionally exercises #533 capture/retention/locking and cancellation.

`python3 tools/compose/qualify_restore_daemon.py` independently proves the restart-policy
fence across a real restart of a disposable nested Docker daemon, with a positive restart
control. It requires privileged fixture containers and local Docker binaries; it has no
host Docker socket or network, and does not restart the host daemon.

No fixture evidence qualifies a real NAS, production host, M6 cutover, public stable
distribution or whole-system #603 closure.
Historical release acquisition, native migration, ARM and #604 remain separate.

## Immutable local release bundles

`release inspect /absolute/bundle` checks strict v1 metadata, exact inventory, hashes,
modes and link-free files without Docker/DB access. `release target /absolute/bundle project`
exports schema-3 trusted target facts. The required snapshot field is zero (no capture
observation); restore ignores it. Legacy schema-2 archives pass only the exact-release
bridge accepted in #701. Never configure capture from this target-only evidence.

Root-only commands are `release verify-images`, `release import` and `release adopt`,
each with one absolute trusted directory. Executable image probes require root-owned,
non-writable input ancestry. Import may retain unavailable images but reports
`not-qualified`; adoption requires all images/payloads verified and a completed matching
installation. It preserves UUID/project/hostname, secrets, backup policy, storage and
all runtime inputs. It adds schema 4 and a release reference atomically. Adoption must
use the exact bundled operator. Legacy source evidence corroborates actual bytes;
historical archives and persisted backup source policies are never rewritten.

Installed paths are `releases/vX.Y.Z/` or `releases/candidate-vX.Y.Z-FULLSHA/`.
Different bytes at an occupied name fail. `.stage-ID` plus a root-owned sibling receipt
records interrupted placement; `release reconcile .stage-ID` publishes only a complete
matching stage. Partial stages are retained as evidence, never selected or auto-deleted.
Directory import remains the offline seam. `release unpack ARCHIVE EMPTY_PRIVATE_STAGE`
uses the bounded in-process extractor for trusted offline authoring/bootstrap preparation;
it never imports or pulls. The authoring overload `release unpack X.Y.Z STAGE`
uses the same anonymous metadata/digest/staging owner to inspect a public prior source
without executing its payloads. Assembly emits deterministic tarballs and external checksums.
Checksums do not authenticate publishers.

Keep a stable bootstrap executable at a fixed root-owned path. Invoke
`/usr/local/lib/wayfarer-bootstrap/wayfarerctl --deployment-root /etc/wayfarer dispatch COMMAND`.
A local wrapper may supply that fixed prefix with literal arguments; no cwd/PATH or
manifest-provided path selects the child. The bootstrap is never replaced by lifecycle
commands. Ordinary dispatch selects validated installation authority. Restore resume
and abort select the receipt's original retained owner and verify its exact executable
hash; direct invocation also enforces that owner. Existing receipts without owners
retain their explicit original-operator recovery path. Operator inspect/use/resume
support is separate from application version ordering; unknown protocols fail closed.

Adopted in-place restore reconstructs target evidence from retained release bytes,
without reading the current DB or trusting archive metadata. Clean-root restore may use
`release target` output with the existing `--target-evidence` and explicit capture/restore
payload options. The target still requires provenance acknowledgement before SQL and
application-owned offline candidate validation before activation.

Public stable acquisition is described below. There is no release/image pruning or implicit activation.
The [shipped offline instructions](../tools/release/INSTALL.md) describe layout and
commands. Run the existing Compose recovery qualifier with `--release-bundle PATH`
to include import, adoption, placement recovery and retained operator/target evidence.

Historical capture pairs can be retained via the assembler's explicit
`--capture-directory` and `--capture-evidence` inputs. Actual files and local image
identity remain mandatory; installation evidence only corroborates them. The optional
fixed `capture/` inventory binds their hashes and historical worker version/status.
It stores neither UUID/project nor old Quartz identity. `release corroborate BUNDLE SOURCE`
checks that independent source evidence against either retained capture profile.
`release target BUNDLE PROJECT current` explicitly selects the new pair; the default
selects the historical pair when present. Existing backup policies remain unchanged.
Legacy images without the #701 property require the pinned accepted Quartz SQL resource;
unknown legacy resource contracts are rejected. Full application/source/image/migration
identity and post-restore product validation remain mandatory.


## Trusted-local managed forward update

Use an already extracted, administrator-trusted local `release.json` bundle and
already present immutable images with `--bundle`. Public planning can acquire these
inputs first; the lifecycle performs no PostgreSQL major upgrade, reference seeding
or native migration.
The current installation must already have schema-4 retained release authority,
a compatible retained operator, and an enabled, usable recovery destination.

```sh
wayfarerctl update --bundle /trusted/extracted/target-bundle --plan
wayfarerctl update --accept-plan <printed-sha256>
wayfarerctl update --resume <operation-uuid>
wayfarerctl update --abort <operation-uuid>
```

The canonical plan binds installation/project/storage, exact current and target
release fingerprints, old configuration bytes, credential fingerprints, migration
delta, capacity requirement and retained operator owner. The target must be strictly
later and explicitly authorize the exact source fingerprint/version/schema. Current
complete migrations must be an exact ordered prefix. Same-version repair, downgrade,
image-only replacement, unknown boundaries and required reference seeding are refused.
Planning stages local evidence without stopping services or changing application data.
Execution always requires the exact plan hash; there is no generic `--yes`.

The protected `recovery-control/update.json` records these forward phases:

```text
Authorized -> Fenced -> RecoveryVerified -> MigrationStarted
-> MigrationConfirmed -> ActivationIntent -> TargetActivatedStopped
-> WritesPossible -> PostflightConfirmed -> Accepted
```

Host operation exclusion precedes shared recovery exclusion. The operator records
restart policies, fences app/Quartz/scheduler/proxy writers, and requires a **fresh,
verified, transactionally held quiesced recovery set**. A recent archive is never a
substitute. Pre-migration resume recaptures when continuous fencing cannot be proved.
After `MigrationStarted`, the pre-update archive is never replaced.

`MigrationStarted` is durable before the single named target-image maintenance
container can launch `dotnet Wayfarer.dll database migrate`. It runs as UID1654 with
a read-only root and app-data/DP mount, app-role secret, internal DB network, bounded
resources and no public/provider network, Docker socket or archive destination.
Lost acknowledgement remains mutation-uncertain. Resume inspects the exact helper
and independently validates complete target EF/Quartz, DB and secure readiness;
it never blindly reruns migration. Partial or incompatible state requires restore.

Forward update preserves the same DB/app-data/cache generation. Immutable target
Compose inputs and backup bindings are prepared before one atomic installation
pointer commits the new release. The target starts with restart disabled and ingress
fenced. Offline and private postflight precede ingress, endpoint checks, scheduler
resumption, restart-policy restoration and durable acceptance. `status` and `doctor`
report unresolved update intent even during an installation-pointer transition.
Ordinary mutating commands refuse unresolved intent.
For an interrupted delegated capture, use the owning update's resume or pre-migration
abort. Recovery accepts only the generated root:1654 mode-0640 reservation with exact
capture/hold facts and reconciles the named helper's Compose/image/token ownership
before clearing the reservation under recovery exclusion. Unknown Docker state or
unsafe/foreign authority remains blocking; retain the reservation and held evidence
until it can be reconciled. Do not change permissions or remove it to force continuation.
After resolution, use `wayfarerctl backup configure --recover` for an exact stale
worker exclusion marker. Forward acceptance requires the operation's completed
plan hash; resolution by restore requires the exact accepted restore ownership
join and completion UUID. Recovery preserves `RestoreAccepted` and retained history.

Abort is permitted only before migration may have started and after old authority
and delegated recovery are reconciled. It retains held evidence and leaves services
stopped for explicit `start`. After migration, an old image is **not database
rollback**. Explicit recovery is:

```sh
wayfarerctl update --restore <operation-uuid>
# If interrupted after ownership transfer:
wayfarerctl dispatch restore --resume <restore-operation-uuid>
```

This decision durably joins the update and #695 restore receipts to the held archive
and retained old ReleaseAuthority. It restores into a fresh paired generation while
lifecycle mutation stays fenced. After archive preparation and immediately before
receipt transfer, the existing update exclusivity check rejects newly attached
foreign durable-volume or backend-network consumers under recovery exclusion. Update intent is retained, and recovery completion
is recorded separately from forward `Accepted`. Resume dispatch selects the exact
receipted operator, even after the active release changes; executables are never
replaced in place. Keep old bundles, operator/recovery payloads, image digests,
configuration evidence, receipts and held archives. Cleanup is out of scope.

Stable public release-to-release acceptance remains deferred. Disposable candidate
qualification uses an explicitly compiled `UPDATE_QUALIFICATION` operator restricted
to the existing random temporary recovery fixture; ordinary published operators
reject candidate update execution. The controlled test migration is not a product
migration or an official stable release.


To reproduce the real migration boundary from a clean exact head, first build the
source image and candidate bundle with the existing `tools/release/image.py dry-run`
and `tools/release/bundle.py` commands, then run:

```bash
python3 -B tools/compose/qualify_update.py --source-bundle /absolute/source-candidate --output /absolute/new-update-output
```

The recipe clones that head into a disposable local checkout, increments only its
patch version, and adds `20990101000000_UpdateQualification`, which creates one
empty table through EF. Fixed commit identity/time and the exact parent make the
target source reproducible; `evidence.json` records both commits, migration and
actual built image digest. Image bytes can depend on upstream package feeds.
No product migration, Git tag, registry push or stable release is created. Only
the output candidate copies receive the bounded qualification operator and exact
source boundary. The original source bundle stays unchanged.

Use the emitted `sourceBundle` and `targetBundle` as `--release-bundle` and
`--update-bundle` in the existing `tools/compose/qualify_recovery.py` command,
with its usual published worker, inspector, probe and source app digest. The
application-image workflow contains the complete commands and runs both the
existing recovery qualification and this update journey on lifecycle-sensitive
exact PR heads. Its update observations include real migration failure, lost
acknowledgement, private postflight failure, foreign-consumer refusal before
restore ownership transfer, and forward update followed by post-update restore.


## Public stable acquisition and update planning

```sh
wayfarerctl release acquire X.Y.Z
wayfarerctl release acquire latest
wayfarerctl update --plan
wayfarerctl update X.Y.Z --plan
wayfarerctl update --bundle /trusted/extracted/bundle --plan
wayfarerctl update --accept-plan <printed-sha256>
```

`release acquire` downloads one exact public stable Wayfarer deployment asset. Discovery
is only GitHub's project release-by-tag/latest endpoint. The first supported Compose
lifecycle/update baseline is v1.9.21. Future operators reject exact public selectors
below it through the shared acquisition owner, including `setup --version`,
`release acquire` and public update planning; `latest` still resolves normally.
The immutable published v1.9.21 operator may still accept an explicit 1.9.20 request,
and its historical source metadata is retained. Use only supported releases; see the
[support and native migration boundaries](23-Versioning.md#public-acceptance-and-availability).
Operator protocol/historical recovery floors are separate authorities.
Strict metadata requires exact
tag/name/project, non-draft/non-prerelease status, one uploaded versioned asset,
bounded nonzero size and GitHub REST's SHA-256 asset digest. No token, cookies,
installation metadata or arbitrary URL/repository/channel is accepted. The checksum
sidecar is human/offline integrity evidence and cannot authenticate the publisher.

HTTPS download starts at the deterministic project URL and permits only the fixed
GitHub release-asset CDN redirect. Connect, request and read deadlines are finite;
cancellation reaches streaming, extraction and Docker. The download is limited to
512 MiB; metadata to 1 MiB; uncompressed archive to 768 MiB and each payload to
256 MiB (manifest to the existing 128 KiB bound). Only the fixed regular-file USTAR
inventory is accepted. Links, special/sparse/extension entries, path aliases, duplicate
names, nonzero trailers and overwrite attempts fail. Archive mode bits are ignored.
Private root-owned staging receives fixed contract modes, then `ReleaseBundle.Validate`
and `ReleaseStore.Import` grant immutable retained authority. Download/extraction
failure leaves existing complete releases intact; abandoned `.acquire-ID` directories
are non-authoritative and may be removed by the administrator after confirming no
acquisition is active. Existing `.stage-ID` import reconciliation semantics are unchanged.

Only the bundle's immutable application/accepted DB/Caddy references are pulled,
with an empty Docker credential configuration, then `ReleaseImagesVerifier` rechecks
the images/payload protocols. Pull/verification failure retains the validated bundle
as not execution-ready. Success reports path, fingerprint and version. No setup,
migration, activation, pointer switch or bootstrap replacement is performed.

`update --plan` acquires latest; `update X.Y.Z --plan` acquires that exact version.
Both feed the existing #704 local planner and receipts. Latest being current/older,
source-only, or lacking the exact current fingerprint fails without fallback target
search. Destructive execution still requires the printed plan hash. Offline import/
planning remain available when public acquisition is unavailable.

A baseline bundle with no supported source fingerprints permits fresh setup/restore,
not an update from source-only history. Update planning requires the exact installed
fingerprint in the target's supported sources. Maintainer source-boundary selection
belongs to [stable publication](27-Application-Image-Publication.md#stable-compose-distribution).

Compose fresh setup and restore use PostgreSQL 18.6 with one durable DB volume at
`/var/lib/postgresql`; the official image owns its nested `18/docker` PGDATA.
Fresh setup requires canonical `release.json` so its exact native DB manifest comes
from reviewed release metadata. Raw-template fresh setup cannot fall back to the
historical PG17 digest. Explicit raw clean-root recovery retains its existing
trusted target-evidence and exact DB-digest requirements; it does not upgrade majors.
Native/development PostgreSQL requirements are unchanged.
