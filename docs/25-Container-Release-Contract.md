# Container and release contract

Normative design for [#638](https://github.com/stef-k/Wayfarer/issues/638), under
[#603](https://github.com/stef-k/Wayfarer/issues/603). Investigated 2026-09-25 against
main `d49deb52aa0ff29121ffaca807fc522ba8877d4c`. This records the architecture for
later children; it does not announce an available Docker distribution. Independent
exact-head review and maintainer acceptance remain required before merge.

## Support boundary

The first bundle targets Linux Docker Engine with Compose v2, `linux/amd64`, one
host, one Wayfarer instance and one bundled database. Use a supported Docker host
with local filesystems supporting Unix ownership, atomic rename and durable writes.
The host need not be Ubuntu; Ubuntu 24.04 is the application image OS. WSL is a
development environment, not separate production qualification. ARM, Docker Desktop,
Swarm, Kubernetes, HA and shared/network database volumes are outside initial support.
No host .NET, Node, Python, PostgreSQL, Nginx or Certbot is required by the bundle.
Native/manual deployment remains available through [Deployment](20-Deployment.md).

## Application image

Use `mcr.microsoft.com/dotnet/aspnet:10.0-noble` (Ubuntu 24.04, glibc, full runtime)
and `mcr.microsoft.com/dotnet/sdk:10.0-noble` for the build stage. Each release must
resolve supported patched versions and pin base digests; floating tags below identify
families, not reproducible deployment inputs. Refresh patches through a tested new
release, never by rebuilding an already published release identity. Reassess the
family before upstream .NET/OS support ends. Do not use Alpine, chiseled/distroless,
or the Playwright test image as the initial final application base.

Publish framework-dependent Release `linux-x64`, without trimming, AOT or single-file
packing. Build Node 24/npm and MvcFrontendKit assets before publish; retain generated
CSS, `wwwroot/dist`, Trip Editor manifest/assets, compiled Razor, docs,
`frontend.config.yaml`, EF migrations and embedded `Scripts/tables_postgres.sql`.
The final image has ASP.NET, published payload and browser dependencies, no SDK,
npm, general-purpose Node installation or PowerShell. Playwright's own bundled Node
driver **is a runtime dependency** and must remain in the published `.playwright`
payload. Build-time browser installation tooling must not remove that driver.

Use the .NET image's `app` identity, UID/GID 1654, explicitly selected for runtime.
`/app` and `/opt/wayfarer-browsers` are root-owned, readable/executable by app, never
application-writable or overlaid by state mounts. Work directory/content root is
`/app`; Kestrel listens on internal HTTP port 8080. All writable paths are explicit.

Provision Chromium at image build time using the **published release's** Playwright
installer and its `install-deps chromium` package set on Noble. Copy/install browser
binaries under `/opt/wayfarer-browsers`; set `PLAYWRIGHT_BROWSERS_PATH` to that path.
Microsoft.Playwright 1.62.0 currently requires Chromium/headless-shell revision 1234
(151.0.7922.34); derive future revisions from its shipped `browsers.json`, not this
snapshot. Include fonts and Linux libraries, particularly `libasound2t64` providing
`libasound.so.2`. No runtime download, system-Chromium fallback or browser cache volume.

## Filesystem and volume authority

These are container targets; logical names are Compose volume keys, project scoped.

| Location | Default backing and owner | Classification |
| --- | --- | --- |
| `/app`, `/opt/wayfarer-browsers` | Immutable image, root | Release payload |
| `/var/lib/wayfarer` | `app-data`, 1654:1654 | Durable uploads/imports and complete key ring |
| `/var/cache/wayfarer` | `app-cache`, 1654:1654 | Rebuildable tiles, images, thumbnails |
| `/var/log/wayfarer` | `app-logs`, 1654:1654 | Operational logs, bounded retention |
| `/tmp/wayfarer` | Disposable tmpfs, 1654:1654 | Browser/application temporary work |
| `/var/lib/postgresql/data` | `db-data`, upstream postgres identity | Authoritative PostgreSQL 17 cluster |
| Caddy `/data`, `/config` | `caddy-data`, `caddy-config`, image-appropriate owner | TLS/account state and generated proxy state |
| `/run/secrets` | Selected read-only protected file mounts | Secrets, never image content |

Set `Storage__DataRoot`, `Storage__CacheRoot`, `Storage__LogRoot`,
`Storage__TempRoot` to the four roots above; also set `TMPDIR=/tmp/wayfarer` for
Playwright-owned profiles. Existing derived paths remain `uploads/imports`,
`data-protection`, `tiles`, `images`, `thumbnails/trips`. Complete Data Protection
ring + matching DB + durable uploads form the recovery boundary; `app-cache` being
persistent does not make it authoritative. Cache reuse must preserve the existing
filesystem/DB metadata reconciliation contract; #533 owns archive implementation.

The application name remains exactly `Wayfarer`; fresh bundles explicitly select
`DataProtection__KeyRingPath=/var/lib/wayfarer/data-protection`. Never copy one key
and call that the ring. Preserve F1 preparation/F2 stable ciphertext requirements in
[provider migration guidance](24-Personal-Location-Providers.md#f1-preparation-to-f2-activation).
Old native `Uploads`, `TileCache`, `ImageCache` are bounded **writable** migration
inputs, not default container `/app` volumes. Neither startup nor image replacement
converts their absolute DB references or deletes them; #604 owns real cutover.

Setup prepares named volumes with bounded one-shot ownership initialization before
running app as non-root. Never recursively chown arbitrary administrator paths.
Data/key directories must exclude other users (0700; key files 0600); logs/cache
may use 0750. Mount overrides require explicit absolute paths, ownership preflight
and preservation semantics; default durable/cache/log state uses named volumes.
Only config, secrets and an explicitly chosen backup destination use bounded bind
mounts. Backups live outside the live data volumes; mount them only into the later
backup operation, not the ordinary web service. No NAS mounting/credentials framework.

## Database contract

Select project-maintained `docker.io/postgis/postgis:17-3.5-alpine` for fresh
clusters, pinned by digest per bundle. PostgreSQL **17 only**, PostGIS **3.5.x** and
the PG17 data-volume path remain fixed. #644's [exact image investigation and
qualification](28-Production-Compose.md#exact-third-party-image-decision) refines
only the base family: the Debian candidate still contains unmaintained PG17.5;
the selected Alpine3.24.1 image contains PG17.11/PostGIS3.5.7. Fresh clusters use
UTF8/C.UTF-8 locale. Native/Debian data directories, libc locale/index assumptions and
binary extensions are not interchangeable with musl; logical migration remains
separately owned by #604. Backup tools run in the selected version-matched DB image.
Recheck live patch/support status and qualify before every new bundle release.

Require `postgis` and `citext` in the application DB, checked by the explicit
maintenance operation. PostGIS initialization supplies spatial extensions; citext
is supplied by PostgreSQL and must be enabled explicitly. Do not grant the web
role superuser: setup uses a separate protected administrative credential for DB/
extension provisioning and grants the application/migration role required schema
rights. Readiness uses bundled `pg_isready`; this only proves connection acceptance,
not credentials, extension availability, schema compatibility or application health.

No host DB port is published. Never mount this PG17 directory into another major.
Ordinary Wayfarer updates keep the DB major fixed; PostgreSQL major upgrades need a
separate explicit backup/restore or pg_upgrade contract and qualification. PostGIS
extension upgrades likewise require an explicit tested step, not tag drift.
External PostgreSQL remains possible for native/manual installations; a managed
external-DB mode is deferred and must prove the same extensions, permissions,
version/backup compatibility and readiness before claiming support.

## Release and bundle identity

Registry authority is `ghcr.io/stef-k/wayfarer`. Stable tags are `vX.Y.Z`, matching
the Git tag/GitHub Release and compiled `Version.props` version `X.Y.Z`. Record the
full source SHA independently: informational version deliberately omits it. Never
retarget a stable tag or overwrite its bundle; patched dependencies require a new
release. No `latest` alias is required initially. If aliases are added, they are
convenience pointers only and never update/restore authority. Prerelease SemVer tags
and `dev-<full-sha>` images are opt-in, unsupported for ordinary stable lifecycle;
neither can replace a stable version identity.

Every stable release binds:

- human release version/tag and full source SHA;
- compiled app version and OCI source/revision/version labels;
- immutable Wayfarer manifest digest and `linux/amd64` platform (record index and
  selected platform digest separately if an index is published);
- exact PostgreSQL/PostGIS and Caddy image digests;
- bundle checksum, bundle contract version and configuration schema version;
- required CLI version, expected terminal EF migration and explicit supported
  source release/schema boundaries for update/restore.

Do not infer restore compatibility from SemVer or image availability alone. Unknown
bundle/schema/architecture combinations fail closed. Restore uses the recorded
matching image first; any subsequent upgrade is a separate managed operation.
A digest proves identity, not publisher trust: obtain metadata from the project's
release channel and verify checksums against that trusted release metadata.

Publish `wayfarer-vX.Y.Z-linux-amd64.tar.gz` and `SHA256SUMS` as version-matched
GitHub Release assets. The inspectable bundle contains `compose.yaml`, non-secret
configuration template/schema, Caddy template, `release.json`, operator docs, the
self-contained `wayfarerctl` Linux x64 executable and minimal install/bootstrap glue.
Start `bundleContractVersion` and `configurationSchemaVersion` at 1. `release.json`
owns the fields above and the CLI compatibility requirement. Exact JSON serialization
is owned by the release/bundle child; consumers must reject unsupported contract
versions, not silently improvise. No source clone or host .NET/Python is needed.

GHCR packages must be explicitly public and verified by anonymous digest pull from
a clean client. A public repository alone is insufficient. Publishing uses scoped
Actions `GITHUB_TOKEN` with `packages: write`, linked to the source repository;
ordinary operators need no registry credential. Private testing may use appropriate
read access, but cannot qualify public installation. The application-only publication
pipeline is documented in [image publication](27-Application-Image-Publication.md);
its first real stable release remains the public-distribution acceptance gate.

## Configuration, secrets and project identity

Default administrator-owned install root: `/etc/wayfarer`, configurable once during
setup. Store immutable extracted bundles in `releases/vX.Y.Z/`, non-secret deployment
inputs in `deployment.env`, secrets in `secrets/`, generated installation identity
in `installation.json`. `wayfarerctl` locates that root explicitly or by the default,
then invokes Compose with absolute bundle/config paths and explicit `--project-name`.
Do not derive identity from the caller's current directory. Default project name is
`wayfarer`; a setup-time alternative must match `[a-z0-9][a-z0-9_-]*` and be persisted.
Do not rename a project during updates, which would silently select fresh volumes.

Stable service keys: `wayfarer`, `db`, `caddy`. Maintenance uses one-shot commands on
the `wayfarer` service/image, not a second independently versioned migration image.
Use Compose service/project labels and APIs/commands; generated container names and
`container_name` are not public interfaces. Stable networks are `backend` (internal,
DB + app) and `edge` (app + managed Caddy, outbound access). App needs egress for
providers/tiles; do not isolate all its networks. Caddy has no DB network access.

`deployment.env` contains public hostname, proxy mode, contact email, install/project
identity and backup destination path/policy, never passwords. Container settings
include Production environment, internal HTTP binding, explicit Storage/browser
paths, AllowedHosts and narrowly scoped trusted proxy configuration. Managed mode
only publishes Caddy 80/443. External mode omits Caddy and must use an explicitly
bounded loopback/private application binding and trusted proxy authority; detailed
forwarded-header/security implementation belongs to the readiness/proxy children.

Secrets are protected generated files, directory 0700 and files 0600, mounted
read-only only into consumers under `/run/secrets`. DB uses `POSTGRES_PASSWORD_FILE`;
Wayfarer needs an explicit file-based connection-secret reader in the readiness
child (ASP.NET configuration does **not** automatically support arbitrary `_FILE`
variables). Local Compose file secrets are bind mounts, not an encrypted secret
store: provision host ownership/readability for the actual container UID, and do not
assume Compose `uid`/`gid` remaps file ownership. Never put secrets in argv, image
layers, examples, deployment.env, interpolation output, logs or doctor diagnostics.
Administrator password bootstrap/reset uses protected input/stdin, never the existing
password-argv CLI. Generated metadata records digests/config versions/project state,
contains no secrets and is reconstructable from verified bundle/runtime evidence.

## Lifecycle ownership to implement

These are required future behavior, **not commands/endpoints available on main**.
Keep maintenance in the application executable using existing EF/seeding authorities;
no SDK/EF tool installation, shell SQL migration engine or parallel domain logic.

| Operation | Required owner and failure boundary |
| --- | --- |
| Dependency readiness | Compose DB health + bounded CLI waits; no schema mutation |
| `database migrate` | Target app image, one-shot offline maintenance; EF migrations and Quartz schema resource; exit nonzero on failure |
| `database seed` | Same image, explicit idempotent reference/role seed; separate from web startup |
| Administrator bootstrap | App Identity authority via protected input; secure protected admin before ingress/readiness |
| Web start/restart | Validate config, Storage/DP authority, DB/extensions/schema and secured bootstrap; no automatic migrations or admin creation |
| `/health/live` | Cheap process/HTTP responsiveness; no external provider/browser work |
| `/health/ready` | Bounded DB query, expected schema/extensions and completed secure bootstrap; sanitized 503 on failure |
| `healthcheck` CLI | Same published executable makes a bounded loopback readiness request; exit status for Docker, no curl/wget dependency |
| `wayfarerctl doctor` | Cross-service/image/config/mount/DB/public HTTPS diagnostics; representative browser check only explicitly requested |

Setup orders DB/extension readiness → migrate → seed → secure admin → start web →
readiness → enable public ingress. Seed must not leave the known native default
password as an accepted ready state. Update/restore must quiesce app and Quartz
writers, verify the recovery boundary before mutation, run target-image maintenance,
and activate only after success. Maintenance never starts web/jobs. Serialize these
operations under the later lifecycle owner's installation lock; do not race two
maintenance containers. Failed migration/seed/bootstrap leaves ingress closed and
app stopped/not ready. No automatic downgrade after a partially applied migration.
A restart cannot masquerade as an authorized update or restore.

Use exec-form process launch so SIGTERM reaches ASP.NET; drain requests and await
Quartz job shutdown within an explicit stop grace period (initial target 60 seconds,
subject to job qualification). Forced termination/timeouts must be reported truthfully.
Non-root app, no privileged mode and no Docker socket are baseline requirements.
Read-only root, capability dropping, seccomp/user-namespace sandbox, init/reaping and
private shared-memory sizing require final browser-image qualification. Do not copy
Playwright testing recommendations such as host IPC or SYS_ADMIN into production
without evidence. Preserve the existing browser behavior in this contract slice.

## Evidence and implementation gaps

Current-main inspection: `Wayfarer.csproj` targets net10.0 with Playwright 1.62.0;
`Version.props` and latest GitHub Release are 1.9.19/v1.9.19. Publishing is currently
framework-dependent by default; frontend builds are explicit in `deployment/deploy.sh`.
`Program.cs` validates DP, installs Quartz tables, seeds DB and starts hosted jobs;
`ApplicationDbContextSeed` calls `MigrateAsync` and creates the default admin.
There is no dedicated health endpoint/healthcheck command. `/api/version`, version
CLI and response header expose compiled version, not readiness or image identity.
Current password reset accepts argv. These are readiness-child changes, not claims
that wrapping today's binary in Compose is safe.

`StoragePaths` uses platform user/XDG defaults outside Production; Production supplies
the four roots above. F2 globally selects `Wayfarer` and stable ciphertext; explicit
native ring overrides and bounded previous-default handling remain authoritative.
The only current Actions workflow is PR `tests.yml`, with a documentation-only fast
path; there is no release/image publishing pipeline. `tools/release/version.py`
checks metadata/tags/releases but does not publish them.

Reuse [#631 / PR #632 evidence](https://github.com/stef-k/Wayfarer/pull/632): published
read-only application startup/static serving and real thumbnail production succeeded
with revision 1234 and external logs/key ring, unchanged publish-tree hashes, after
supplying missing Noble `libasound2t64`. Its 14 focused and 3,285 PG-attached tests
are retained evidence, not rerun here. No duplicate app-image spike is needed to
choose the same glibc/Noble/browser dependency family. That native/disposable proof
is **not** final container, sandbox, resource-limit or full-root-read-only qualification.
The image child must build the actual pinned image and repeat representative launch,
non-root/write-boundary, health and browser evidence before release support is claimed.

### Upstream evidence checked 2026-09-25

- [.NET 10 Ubuntu default](https://learn.microsoft.com/en-us/dotnet/core/compatibility/containers/10.0/default-images-use-ubuntu)
  and [image variants](https://learn.microsoft.com/en-us/dotnet/core/docker/container-images):
  full Noble family avoids adding glibc/ICU/browser compatibility work to Alpine/chiseled.
- [.NET source snapshot](https://github.com/dotnet/dotnet-docker/tree/29ebb4c118c30760b93f28fbfbf3dd300a6b65ee/src/aspnet/10.0/noble/amd64):
  current Dockerfile references 10.0.12. Live manifest inspection resolved Noble AMD64
  digest `sha256:ed6a2d26633ddcd3d42a1d9f9866214ecbbc11ba6ac5e0e843da02c13da24072`.
  This is investigation evidence, not a permanently mandated release digest.
- [Playwright Docker guidance](https://playwright.dev/dotnet/docs/docker) and
  [version-coupled browser installation](https://playwright.dev/dotnet/docs/browsers):
  Noble supported; package/browser matching required. The SDK-oriented test image is
  not the selected minimal ASP.NET runtime. Local NuGet `browsers.json` confirms 1234.
- [PostGIS source snapshot](https://github.com/postgis/docker-postgis/tree/2bcd236e3af9ec6e668db51eb37162a79f0eaeaa):
  README lists AMD64 and 17-3.5; Dockerfile uses postgres:17-bullseye with PostGIS
  3.5.2. [PostgreSQL image contract](https://hub.docker.com/_/postgres) documents
  file-secret support and empty-volume-only initialization;
  [citext](https://www.postgresql.org/docs/17/citext.html) is a supplied extension.
- Disposable database proof: pulled `postgis/postgis:17-3.5` at digest
  `sha256:01a6a70e41e6c4467c8f55f6063555ed72db2d6662cd0d571040d42eadaeb6f6`,
  ran with network disabled, no published ports and tmpfs PGDATA. SQL reported
  PostgreSQL 17.5, PostGIS 3.5.2 and successfully created citext 1.6. Container was
  stopped/removed. This proves extension availability, not current security fitness:
  the observed PG17 patch is older than current upstream 17.x metadata. The image
  child must obtain a maintained/patched artifact in this family or explicitly
  revise the selection before publication; this digest is not approved for release.
- Names/topology were parsed with Docker Compose 2.40.3 `config --quiet` using a
  disposable syntax-only model; no application Compose stack was started or shipped.
- [GHCR](https://docs.github.com/en/packages/working-with-a-github-packages-registry/working-with-the-container-registry):
  public anonymous pulls, digest references and scoped Actions publishing supported.
- [Compose project identity](https://docs.docker.com/compose/how-tos/project-name/)
  defines explicit project selection;
  [Compose secrets](https://docs.docker.com/compose/how-tos/use-secrets/) describes
  per-service read-only file delivery. These capabilities do not implement the bundle.

#533 redesign, production Docker/Compose/CLI, image publication, external proxy
qualification, backup/restore/update and #604 M6 migration remain with later children.
No runtime/configuration/database changes are introduced by this document.
