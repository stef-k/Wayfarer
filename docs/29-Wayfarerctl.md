# Operate Wayfarer with wayfarerctl

`wayfarerctl` is the Linux AMD64, self-contained C# operator executable introduced
by #648. It orchestrates the [accepted Compose substrate](28-Production-Compose.md)
and existing application maintenance commands. This foundation includes fresh setup,
lifecycle, diagnosis, logs and essential user recovery. **Backup, restore, update,
uninstall and native migration are not implemented.** #603 is not complete; the
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

A failed setup retains files and volumes. Reinvocation refuses existing/partial state;
there is no automatic rollback, password replacement, migration downgrade or purge.
Preserve everything and inspect `status`, `doctor`, and bounded service logs first.
The failed step is printed without child exception/config dumps. A competing invocation
is serialized by the installation lock; do not delete a live lock to bypass it.

For deliberate advanced recovery, use the same installation identity, bundle, project
and protected credentials with the [manual maintenance sequence](28-Production-Compose.md#advanced-fresh-initialization).
Keep web/ingress stopped while completing schema/seed/bootstrap. Do not bootstrap an
already-existing admin again; use the application's explicit protected reset seam only
when password recovery is intended. Never reset DB credentials by replacing files.
After securing admin, start the configured stack and run doctor. If the **only** failure
is the absent completion marker, a root administrator may explicitly create a0600,
root:root `setup-complete` file containing `1` and rerun doctor. Any other FAIL remains
unresolved. This manual acknowledgement is not an automated resume/update facility.
If config/secrets were only partly created, recover from retained verified files with
an administrator; do not infer that missing config means empty database state.

| Symptom | Action |
| --- | --- |
| Docker unavailable/denied | Verify local Engine service/socket and root access; remote contexts are intentionally ignored |
| Compose old/missing | Install supported Compose v2 through the host's normal administration path |
| Bundle/config rejected | Restore complete trusted files; reconcile installation/env identity, ownership and links |
| Network conflict | Before fresh setup choose a free private `--edge-prefix`; inspect VPN/host routes |
| Port conflict | Free managed80/443 deliberately or use external mode with a free loopback port |
| DB unhealthy | Inspect `logs db`; preserve cluster/secrets; do not delete volumes or change PG major |
| Migrate/seed/bootstrap failed | Keep web stopped; use application maintenance authority with the same image and protected input |
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
It removes only its random labelled resources. Test-only TLS never changes production
Caddy automatic HTTPS. This is not public-CA issuance, production/native qualification,
backup/restore/update acceptance or completion of #603.
