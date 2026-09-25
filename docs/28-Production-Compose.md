# Production Compose substrate

Implements [#644](https://github.com/stef-k/Wayfarer/issues/644) against
`ae2ece49896049ce65193acc3188a8b887c5f7bf`, preserving the
[container contract](25-Container-Release-Contract.md),
[application image](26-Application-Container.md) and
[publication identity](27-Application-Image-Publication.md).
This is an advanced/internal precursor to `wayfarerctl`, not the completed #603
installation product. Guided setup, backup/restore, updates, release tarball and
`release.json` are not provided. Nothing here migrates a native installation.

## Topology and state

`deploy/compose/` is self-contained bundle source: no source/build mount or local
application toolchain is needed on the target host. Linux AMD64 Docker Engine and
Compose 2.24.4+ with Bash are required. Keep the extracted directory intact.
The default project is `wayfarer`; persist any alternative `-p` project selection
and use it for every command. Generated container names are not interfaces.

| Authority | Services | Exposure/state |
| --- | --- | --- |
| `backend` | `wayfarer`, `db` | Internal network; no DB host port |
| `edge` | `wayfarer`, managed `caddy` | Outbound app/provider access; Caddy at reserved `.3` |
| `app-data` | `wayfarer` | Durable uploads/imports and complete Data Protection ring |
| `app-cache` | `wayfarer` | Rebuildable tile/image/thumbnail state |
| `app-logs` | `wayfarer` | Operational logs with application retention |
| `db-data` | `db` | Authoritative PG17 cluster at `/var/lib/postgresql/data` |
| `caddy-data`, `caddy-config` | `caddy` | Persistent certificates/account and proxy state |

Wayfarer retains UID1654, read-only root, immutable application/browser payload,
512MiB temporary tmpfs and 70-second stop grace (covering the 60-second application
contract). App volumes use `nocopy`: copying the root-owned image directories into
an empty volume would undo explicit ownership preparation. No privileged container,
Docker socket, source tree, or backup destination is mounted.
Caddy has no backend attachment or secrets. Only it publishes 80/TCP, 443/TCP and
443/UDP in managed mode. There is no published Kestrel endpoint in managed mode;
as with ordinary Linux bridge networking, a privileged Docker host can still reach
container addresses. Host administration is outside the network isolation boundary.

## Exact third-party image decision

Live manifest/config, executable and signed-package checks on 2026-09-25 established:

| Candidate | Actual payload / provenance | Decision |
| --- | --- | --- |
| PostGIS project `17-3.5` Debian | PG17.5 (`17.5-1.pgdg110+1`), PostGIS3.5.2, Debian11 | Rejected; current manifest remains stale |
| Official `postgres:17.11-bookworm` + PGDG PostGIS | PG17.11 (`17.11-1.pgdg12+2`), PostGIS3.6.4 (`3.6.4+dfsg-2.pgdg12+1`), Debian12/glibc2.36 | Selected Wayfarer packaging route |
| PostGIS project `17-3.5-alpine` | PG17.11, PostGIS3.5.7, Alpine3.24.1/musl | Qualified alternative, superseded by practical glibc route |
| PostGIS project `17-3.6-alpine` | PG17.11, PostGIS3.6.4, Alpine3.24.1/musl | Available; does not resolve the libc concern |

The project Debian `17-3.6` tag does not resolve; its source Dockerfile is a
placeholder. Current `17-3.5` index remains
`sha256:01a6a70e41e6c4467c8f55f6063555ed72db2d6662cd0d571040d42eadaeb6f6`.
The rejected Alpine selection was
`sha256:894f570c0cf0664ed5576a8fd5d5bfb8fb1b19d592885b686c3a88c8bd90c41f`;
the investigated 3.6 Alpine digest was
`sha256:a8ffa9afeea4ad6eada171fa2afdb57cd3eb90f92ce20156aa2cb8411d70e0cd`.
No third-party convenience image or PostgreSQL major change is introduced.

`db/Dockerfile` pins the official PostgreSQL17.11 Bookworm **AMD64 base manifest**
`sha256:91eb910c44c7ed13f7f1a4ccadaa9ca72ef14cddc04cacb6e070e48eb44731a3`
(index `sha256:639ab7ceb90e13123085b741fb31ef493fba25463002f6da665352e7b534b652`).
It installs exact matching `postgresql-17-postgis-3` and `-scripts` versions from
PGDG using the base's repository/signing key, and rejects a changed server package.
There is no source compilation, replacement entrypoint, runtime package installation
or baked-in cluster/secret. Existing Compose initialization creates only required
`postgis` and `citext` extensions in the application DB. PostgreSQL supplies citext1.6.
The base retains UID/GID999 and `/var/lib/postgresql/data`.

[PostgreSQL17.11 release notes](https://www.postgresql.org/docs/17/release-17-11.html)
identify current PG17 fixes. [PostgreSQL's Debian distribution](https://www.postgresql.org/download/linux/debian/)
supports Bookworm and provides maintained PG17 packages; [PostGIS's installation guide](https://postgis.net/documentation/getting_started/install_ubuntu/)
explicitly recommends this package route. The upstream image is maintained by the
[Docker Official Images PostgreSQL team](https://github.com/docker-library/postgres),
and the extension packages by the PostgreSQL/PGDG packaging ecosystem. Debian owns
base-library security updates. Wayfarer owns the small assembly layer and must
monitor all three, refresh pins, rebuild, qualify, then publish a new immutable
DB image. No unattended update runs inside an existing image. Transitive Debian/
PGDG dependencies are resolved at build time: exact top-level pins do not promise
bit-for-bit rebuilds; the published **derived manifest digest** is runtime authority.
If a pinned package is withdrawn, the build fails rather than silently changing it.

This is lower maintenance than compiling PostGIS or adopting another vendor's
entrypoint/update contract. Canonical/Ubuntu was not needed after the official
PostgreSQL + PGDG route passed assembly; no claim is made that an Ubuntu vendor
image supplies this exact combination. The Alpine alternative delegates assembly
to PostGIS but introduces musl locale/library compatibility work. Both require
Wayfarer to track upstream fixes and requalify each accepted image refresh; the
selected route additionally requires Wayfarer to build and publish that image.

Fresh clusters retain explicit UTF8/C.UTF-8. `citext` folds Greek and accented Latin
case, preserves accent distinctions, and ordinary text ordering is byte ordering,
not Greek-language dictionary ordering. Plain `C` previously failed Greek folding.
Qualification checks these properties and required extensions on the selected base,
then checks them again after custom-format restore with a spatial/citext row and
the real application schema. This is a bounded behavioral sample, not equivalence
of every Unicode case/collation between libc implementations.

For #604, inspect source PG/PostGIS versions, extension usage, encoding, locale
provider/version and collation-dependent uniqueness before logical dump/restore.
Use the selected image's PG17 tools and explicitly provision compatible extensions;
rebuild indexes through restore and verify application identities and representative
Greek/Latin data. The PostGIS3.5 → 3.6 boundary needs source-specific qualification;
this fresh-stack proof does not qualify production migration, downgrade, binary
extensions or direct data-directory reuse. Glibc reduces the libc change but does
not remove these migration requirements. Managed backup/recovery remains unimplemented.

## Derived database image delivery

The production Compose file consumes `ghcr.io/stef-k/wayfarer-db@${DB_DIGEST}` with
no build context. A target host neither builds Wayfarer nor installs PGDG packages.
`DB_DIGEST` must be the published, qualified **derived image manifest**, never the
PostgreSQL base digest, a local image ID or a guessed reference. As with the pending
first application publication, source/CI qualification is not proof of anonymous
registry availability. Until DB publication and digest capture are complete, this
source bundle is not ready for ordinary deployment. The release bundle must carry
the accepted digest; do not retain Alpine as an implicit fallback.

Maintainer/CI assembly and disposable qualification use:

```bash
docker buildx build --platform linux/amd64 --load --provenance=false --sbom=false \
  --tag wayfarer-db:qualification deploy/compose/db
# Both arguments are exact local IDs; only the disposable override consumes them.
python3 tools/compose/qualify.py --image "$application_image_id" \
  --db-image "$(docker image inspect wayfarer-db:qualification --format '{{.Id}}')"
```

Publication must preserve the qualified artifact, record source revision/package
versions and registry manifest digest, then prove an anonymous pull and rerun the
Compose gate against that exact pulled artifact before declaring release acceptance.
Do not rebuild under an existing release identity. Final release tarball/metadata
and general lifecycle publication orchestration remain later #603 work.

Caddy is official `caddy:2-alpine`, executable **2.11.4** on Alpine3.23.6, pinned index
`sha256:6aeddd44c3078b0f9a35206472a11420648a79c184603ef95957d0a20044cb2b`;
selected Linux AMD64 manifest
`sha256:040e9f7480b80b6d4a7e5013a21159b950a63dcbdb956e38abe2387fb28d9ec0`.
No plugins are added. Refresh either image only in a reviewed new bundle/source
revision after live version/security review and this integration qualification.
Never change PostgreSQL major through an image refresh; PostGIS upgrades also need
an explicit tested extension step. Do not rebuild an existing stable release.

## Configuration and protected files

Copy `config/deployment.env.example` to an absolute administrator-owned location,
for example `/etc/wayfarer/deployment.env`. It contains literal `KEY=value` entries,
no shell expansion, quoted values or passwords. Set the public DNS hostname,
application and derived DB `sha256:` digests from genuine release evidence, mode and secret paths.
The application reference becomes `ghcr.io/stef-k/wayfarer@sha256:...`.
The DB reference becomes `ghcr.io/stef-k/wayfarer-db@sha256:...`; Caddy is pinned directly in Compose.
No released application digest is fabricated by this slice; first real publication
acceptance under #642 remains required for ordinary anonymous deployment.

Choose an unused private `172.16-31.x.0/24` via `EDGE_PREFIX`; `.1` is its gateway,
`.3` is Caddy. Do not overlap host/VPN routes or another Docker network. The default
is `172.30.64`. `compose.sh` performs configuration validation and delegates raw
Compose operations; it does not generate secrets or implement lifecycle policy.
It rejects missing/malformed image digests, hostnames, mode, secret paths and
non-loopback external bindings. Run it with an absolute env path for every command.
Advanced raw Compose bypasses this preflight and is not the supported config seam.

Provision cryptographically random, distinct administrative and application DB
passwords of at least 32 bytes outside the repository. Protect the parent directory
with mode0700; files are mode0600 and mounted read-only:

| Input | Host owner | Consumer |
| --- | --- | --- |
| `DB_PASSWORD_FILE` | root (upstream root entrypoint reads it) | DB bootstrap administrator |
| `DB_APP_PASSWORD_FILE` | 999:999 (selected Debian postgres) | DB initialization |
| `APP_PASSWORD_FILE` | 1654:1654 | Wayfarer password-file seam |

The last two files contain **the same application password**, copied with separate
consumer ownership; Compose local file secrets do not remap UID/GID. They represent
one credential, not two roles. Verify matching bytes using an authorized root
session without printing them. Never chmod secrets world-readable. The non-superuser
`wayfarer` DB owner can migrate its own schema; only administrative initialization
creates extensions. Initialization runs only on an empty cluster and never resets an
existing password, runs EF/Quartz, seeds users, or treats `pg_isready` as schema proof.
Changing mounted files is not password rotation for an existing database.

## Advanced fresh initialization

Run only on a **new, disposable or explicitly authorized fresh installation**.
Assume `bundle` names the absolute extracted Compose directory, `env` the absolute
non-secret configuration, and `admin_input` a separately protected password file.
These are internal maintenance seams for the later CLI, not guided setup commands.

```bash
"$bundle/compose.sh" "$env" config --quiet
"$bundle/compose.sh" "$env" up -d --wait db
# Fixed named-volume roots only; no recursive ownership changes or arbitrary host paths.
"$bundle/compose.sh" "$env" run --rm --no-deps --user 0 --entrypoint sh wayfarer -ec \
  'chown 1654:1654 /var/lib/wayfarer /var/cache/wayfarer /var/log/wayfarer; chmod 700 /var/lib/wayfarer; chmod 750 /var/cache/wayfarer /var/log/wayfarer'
"$bundle/compose.sh" "$env" run --rm -T wayfarer database migrate
"$bundle/compose.sh" "$env" run --rm -T wayfarer database seed
"$bundle/compose.sh" "$env" run --rm -T wayfarer admin bootstrap administrator --stdin < "$admin_input"
"$bundle/compose.sh" "$env" up -d --wait
```

Serialize maintenance and keep web/ingress stopped until it succeeds. Ordinary web
startup does not migrate or bootstrap. DB health orders maintenance/web dependency;
application readiness checks compatible schema/bootstrap; managed Caddy starts only
after app health. `pg_isready` does not prove credentials or extensions. Inspect
`ps` and public `/health/ready`; Caddy startup is not public certificate acceptance.

Managed mode uses `PROXY_MODE=managed` and Caddy's normal public automatic HTTPS.
DNS and host firewall must allow its listeners. Its sole upstream is Wayfarer8080;
stream flushing is immediate and no arbitrary body or response timeout truncates
exports. Caddy's default incoming forwarded-header handling protects against public
spoofing; Wayfarer trusts just `.3`, not all edge peers.

External mode uses `PROXY_MODE=external`, `EXTERNAL_PROXY_ADDRESS=<edge-prefix>.1`
and optional `LOOPBACK_PORT` (default8080). Binding is restricted to 127.0.0.1.
This slice qualifies a Linux host-native proxy reaching that loopback port; other
container/private-network proxy topologies are not claimed. The proxy must preserve
the public Host and send authoritative `X-Forwarded-Proto`, `X-Forwarded-Host` and
one client `X-Forwarded-For`, replacing client-supplied forwarding headers. Qualify
the observed hop if host NAT/firewall behavior differs. Caddy has no active profile
in this mode. When switching from managed mode, first stop/remove its service using
the old managed configuration, then stop Wayfarer and activate the external config;
profile omission alone does not stop an already-running container. Switching back
requires freeing80/443 from the existing proxy. No Nginx/Cloudflare-specific app
changes or arbitrary-proxy support is implied.

## Recreation and qualification

Use the same project, configuration and named volumes with `up -d --force-recreate`;
state is not in container layers. Stop writers before DB replacement. Keep DB,
complete key ring and uploads together for recovery; caches/logs are not replacements
for authoritative data. **Volume deletion destroys state.** Do not use volume removal
in ordinary lifecycle commands. No safe update/rollback/restore automation exists yet.

CI reuses the application-image dry-run and runs `tools/compose/qualify.py --image
<local-app-image-ID> --db-image <local-db-image-ID>`. Its test-only override selects that exact local build, an isolated
project, high loopback TLS port and Caddy internal CA. Test configurations disable
CA trust-store installation; curl trusts only the explicitly supplied temporary CA file. Production pins and automatic
public HTTPS remain unchanged. It validates both config modes, malformed inputs,
fresh non-superuser migration/seed/bootstrap, health, real page/static/KML/SSE/PDF/
thumbnail paths, network/mount boundaries, DB/key/upload/TLS state after recreation,
logical dump/restore, authenticated-cookie survival, public client-IP spoof resistance
and a real separate host-native Caddy proxy through the external loopback endpoint. It deletes only its random
project-labelled test resources and temporary secret files, never global Docker state.
CI builds the DB from its pinned upstream base/packages and checks actual DB versions;
Caddy is pulled by its production digest. Registry acceptance of the derived DB remains separate.
Existing image/browser/release and ordinary application CI remain separate gates.
This is disposable integration evidence, not public-CA issuance, production host,
M6 cutover, arbitrary external-proxy, mobile/embed or full #603 acceptance.

Local qualification on 2026-09-25 used Docker29.1.3/Compose2.40.3 on Linux/WSL.
The maintained Debian/PGDG DB passed explicit non-superuser EF/Quartz migration, repeated
seed and protected admin bootstrap. Browser-generated JPEG/PDF, SSE heartbeat and
KML download passed through trusted local TLS. Container replacement preserved DB,
complete keys, a representative durable upload and TLS identities; an authenticated
cookie remained valid. Local derived DB image ID was
`sha256:f072a71e7835871f06219ade7c44fbcb05c90343139effb9d881c384206b6700`;
this is local content evidence, **not a published registry manifest digest**.
The Greek/Latin sample also passed against the former Alpine image; Debian exposes
`C.utf8`, `en_US.utf8` and ICU Greek collations, while the Alpine probe exposed ICU
Greek but neither tested glibc locale name. Production uses the explicit database
locale rather than assuming those named collations exist on every base.
The existing five container configuration tests and 43 release tooling tests passed
in the original qualification; the DB correction reran the complete Compose gate
and focused config rejection checks. Exact-head CI repeats the image build and
Compose integration; see the PR checks for the final source revision's result.

## Derived DB publication and recovery

The bounded `.github/workflows/database-image.yml` manual workflow publishes only
`ghcr.io/stef-k/wayfarer-db`. It does not create an application release, Git tag,
release tarball or `release.json`. Select an independently reviewed full source SHA
containing this workflow, recipe and qualification tools. The selected workflow ref
must resolve to exactly the supplied `source`; checkout stays at that SHA.

The immutable publication tag is
`pg17.11-postgis3.6.4-bookworm-<full-source-SHA>`. It identifies a reviewed DB assembly,
independently of the application release number. Later complete release bundles must
consume its qualified **registry manifest digest** and retained evidence, never its
mutable tag or local image ID. A refreshed recipe requires a new reviewed source and
publication identity. Existing identities are never overwritten, including reruns.

The publisher builds the unchanged pinned recipe, verifies actual Debian package and
PostgreSQL executable versions against OCI metadata, and runs the full disposable
Compose gate (including executable PostGIS SQL, locale/citext and dump/restore) before
push. Only its publishing job receives `packages: write`, using `GITHUB_TOKEN` over
stdin. No PAT is needed. The single AMD64 manifest binds the tested config; OCI and
JSON evidence include source, exact package versions and official base reference.
The workflow serializes DB writes and requires explicit registry absence before build
and immediately before push. Authentication/network/ambiguous errors fail closed.
GHCR has no conditional create-only push: other package writers must not race this
workflow, retarget tags or delete immutable evidence.

`db-publication` JSON is uploaded before the anonymous job starts, even if a
post-push registry check fails. A fresh runner with read-only repository permission
uses an empty Docker client configuration to pull only the captured digest. It checks
platform, OCI/package/executable identity, then repeats the entire Compose gate with
that pulled DB reference and `pull_policy: never`. The DB is never rebuilt in this
job. The application companion is built locally from the same source in each job.
Only successful `db-evidence` records anonymous/full Compose acceptance. Preserve
both artifacts beyond Actions retention with the later bundle's release evidence.

### Maintainer activation and exact-source dispatch

[GitHub requires a manual workflow on the default branch before dispatch](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow).
While #645 remains unmerged, publication is blocked until a separately reviewed and
maintainer-accepted activation makes this workflow available on the default branch.
Do not merge #645 or create a fake application release to bypass this gate. The
implementation environment has no scoped package-write token; local/PR evidence
therefore does not establish a public derived digest. Once activation is authorized,
dispatch the reviewed PR branch/ref with its exact full head (verify it has not moved):

```sh
gh workflow run database-image.yml --ref REVIEWED_REF \
  -f source=FULL_REVIEWED_SHA -f db_version=pg17.11-postgis3.6.4-bookworm
gh run list --workflow database-image.yml
gh run watch RUN_ID --exit-status
gh run download RUN_ID -n db-publication -D /absolute/evidence/db-publication
```

If the new GHCR package is private, preserve `manifestDigest` from that artifact,
then set the **wayfarer-db** package visibility to **public** in GitHub package
settings. [Public GHCR packages support anonymous pulls](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility).
Rerun only the failed anonymous job (`gh run rerun RUN_ID --failed` when it alone
failed), or explicitly recover without any publication job:

```sh
gh workflow run database-image.yml --ref REVIEWED_REF \
  -f source=FULL_REVIEWED_SHA -f db_version=pg17.11-postgis3.6.4-bookworm \
  -f digest=sha256:RECORDED_DERIVED_MANIFEST
gh run download RECOVERY_RUN_ID -n db-evidence -D /absolute/evidence/db-qualified
```

Never rerun the publishing job after a push, including interrupted runs. If digest
recording was interrupted, inspect registry/run evidence; do not rebuild or delete
the existing tag. Authentication failure during the absence check also requires a
maintainer access investigation, not relaxed absence checks. A successful public
pull and full pulled-artifact qualification remain mandatory before #644 acceptance.

PR CI runs `tools/release/db_image.py dry-run` against a clean checkout with the same
build/metadata/full Compose path, without login, package-write permission or push.
Focused publication tests run with the existing `tools/release/tests` selection.
