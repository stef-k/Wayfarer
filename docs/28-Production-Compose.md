# Production Compose substrate

The production Compose substrate follows the
[container contract](25-Container-Release-Contract.md),
[application image](26-Application-Container.md) and
[publication identity](27-Application-Image-Publication.md).
This page owns the Compose substrate and DB publication/promotion procedure.
Maintainers start at [Versioning and Release Operations](23-Versioning.md).
The manual commands below are advanced maintenance seams; ordinary setup, updates,
backup and restore belong to [wayfarerctl](29-Wayfarerctl.md).
Nothing here migrates a native installation.

## Topology and state

`deploy/compose/` is self-contained bundle source: no source/build mount or local
application toolchain is needed on the target host. Linux AMD64 or ARM64 Docker Engine and
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
| `db-data` | `db` | Authoritative PG18 cluster under `/var/lib/postgresql/18/docker`, mounted at `/var/lib/postgresql` |
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

## Request-size ownership

Managed Caddy has no request-body limiter. The application owns its fixed 100 MiB
ceiling and the dynamic Admin policy for the two user multipart upload endpoints.
Trip KML alone adds a section ceiling derived from its existing XML budget.
An external proxy may impose an equal or stricter operator-owned ceiling; a stricter
limit intentionally makes some application-allowed uploads unavailable. It is never
dynamically synchronized with `UploadSizeLimitMB`. Native Nginx guidance uses 100M.
The disposable qualification seeds a 1 MiB upload setting before application startup,
then verifies authenticated, antiforgery-valid Location upload success and a 413
rejection without new durable file/row state through real managed Caddy. Its existing
owner removes the temporary project and volumes, including this qualification state.

## Browser response headers

Managed Caddy passes the application's route-aware CSP, XFO, nosniff and referrer
headers through unchanged. Do not add `header` or `header_down` overrides or duplicate
embed route matchers. Ordinary pages permit only same-origin framing; the two successful
public map embed documents permit external parents. See [Security](21-Security.md#browser-response-headers).
`tools/compose/qualify.py` checks exact singleton header values for ordinary and embed
Trip responses through both managed Caddy and its existing external-proxy fixture.

## Exact third-party image decision

The supported database recipe uses the official PostgreSQL Bookworm image and
signed PGDG PostGIS packages. Exact source pins belong to
[`db/Dockerfile`](../deploy/compose/db/Dockerfile); accepted published DB identities
belong to [`database-release.json`](../tools/release/database-release.json).

`db/Dockerfile` pins the official PostgreSQL18.6 Bookworm **multi-platform index**
`sha256:3725f4e2499eef5134592b3b4ab79a543ed7f8e533b05b5b637af926630f6650`.
Its AMD64 manifest is `sha256:9e73daeb439141c2b11eea2463f5f1a3b269fd90d897b41cddb7cb440f21aa5d`;
ARM64 uses `sha256:4c6516b5d6dfd96a6888541396f76545a63290be0aec2542547d7bcdd7515e28`.
It installs exact matching `postgresql-18-postgis-3` and `-scripts` versions from
PGDG using the base's repository/signing key, and rejects a changed server package.
There is no source compilation, replacement entrypoint, runtime package installation
or baked-in cluster/secret. Existing Compose initialization creates only required
`postgis` and `citext` extensions in the application DB. PostgreSQL 18.6 supplies citext1.8 (confirmed from its installed control file and upstream `REL_18_6` source); exact release/recovery checks require that version.
The recipe requires `postgresql-18=18.6-1.pgdg12+2` and both PostGIS packages at
`3.6.4+dfsg-2.pgdg12+1` on AMD64 and ARM64.
The base retains UID/GID999. Compose mounts the one durable `db-data` volume at
`/var/lib/postgresql`; the official image owns `PGDATA=/var/lib/postgresql/18/docker`.
Stateless probes shadow the declared parent volume with read-only tmpfs. No custom
PGDATA, host-path migration, PG17 physical reuse or major-upgrade path is introduced.

[PostgreSQL18.6 release notes](https://www.postgresql.org/docs/18/release-18-6.html)
identify current PG18 fixes. [PostgreSQL's Debian distribution](https://www.postgresql.org/download/linux/debian/)
supports Bookworm and provides maintained PG18 packages; [PostGIS's installation guide](https://postgis.net/documentation/getting_started/install_ubuntu/)
explicitly recommends this package route. The upstream image is maintained by the
[Docker Official Images PostgreSQL team](https://github.com/docker-library/postgres),
and the extension packages by the PostgreSQL/PGDG packaging ecosystem. Debian owns
base-library security updates. Wayfarer owns the small assembly layer and must
monitor all three, refresh pins, rebuild, qualify, then publish a new immutable
DB image. No unattended update runs inside an existing image. Transitive Debian/
PGDG dependencies are resolved at build time: exact top-level pins do not promise
bit-for-bit rebuilds; the published **derived manifest digest** is runtime authority.
If a pinned package is withdrawn, the build fails rather than silently changing it.

The selected Debian/glibc route avoids changing the native libc family to Alpine/musl.
It still requires Wayfarer to monitor upstream fixes and requalify each image refresh;
shared libc alone does not establish data-directory, locale or extension portability.

Fresh clusters retain explicit UTF8/C.UTF-8. `citext` folds Greek and accented Latin
case, preserves accent distinctions, and ordinary text ordering is byte ordering,
not Greek-language dictionary ordering. Plain `C` previously failed Greek folding.
Qualification checks these properties and required extensions on the selected base,
then checks them again after custom-format restore with a spatial/citext row and
the real application schema. This is a bounded behavioral sample, not equivalence
of every Unicode case/collation between libc implementations.

For [native migration](https://github.com/stef-k/Wayfarer/issues/604), inspect source PG/PostGIS versions, extension usage, encoding, locale
provider/version and collation-dependent uniqueness before logical dump/restore.
Use the selected image's PG18 tools and explicitly provision compatible extensions;
rebuild indexes through restore and verify application identities and representative
Greek/Latin data. The PostGIS3.5 → 3.6 boundary needs source-specific qualification;
this fresh-stack proof does not qualify production migration, downgrade, binary
extensions or direct data-directory reuse. Glibc reduces the libc change but does
not remove these migration requirements. Compose recovery sets preserve the full resolved ring; managed restore validates that paired authority before activation.

## Derived database image delivery

The production Compose file consumes `ghcr.io/stef-k/wayfarer-db@${DB_DIGEST}` with
no build context. A target host neither builds Wayfarer nor installs PGDG packages.
`DB_DIGEST` must be the published, qualified **derived image manifest**, never the
PostgreSQL base digest, a local image ID or a guessed reference. Source/CI
qualification alone is not proof of anonymous
registry availability. Stable assembly reads the reviewed publication evidence in
`tools/release/database-release.json`, verifies its immutable index and both native
manifest/config pairs from GHCR, and records only the selected native DB digest in
`release.json`. The accepted authority covers PostgreSQL 18.6 + PostGIS 3.6.4 on
AMD64/ARM64. Its machine-readable provenance remains in `database-release.json`;
application/bundle publication and public installation acceptance belong to
[application publication](27-Application-Image-Publication.md).

Maintainer/CI assembly and disposable qualification use:

<!-- {% raw %} Preserve Docker's Go template through Jekyll Liquid processing. -->

```bash
docker buildx build --platform linux/amd64 --load --provenance=false --sbom=false \
  --tag wayfarer-db:qualification deploy/compose/db
# Both arguments are exact local IDs; only the disposable override consumes them.
python3 tools/compose/qualify.py --image "$application_image_id" \
  --db-image "$(docker image inspect wayfarer-db:qualification --format '{{.Id}}')"
```

<!-- {% endraw %} -->

Publication must preserve the qualified artifact, record source revision/package
versions and registry manifest digest, then prove an anonymous pull and rerun the
Compose gate against that exact pulled artifact before declaring release acceptance.
Do not rebuild under an existing release identity. Follow
[the DB publication procedure](#derived-db-publication-and-recovery) and
[release orchestration](23-Versioning.md) for publication/promotion sequencing.

Caddy is official `caddy:2-alpine`, executable **2.11.4** on Alpine3.23.6, pinned index
`sha256:6aeddd44c3078b0f9a35206472a11420648a79c184603ef95957d0a20044cb2b`;
selected Linux AMD64 manifest
`sha256:040e9f7480b80b6d4a7e5013a21159b950a63dcbdb956e38abe2387fb28d9ec0`.
The same immutable index selects ARM64 manifest
`sha256:c802bf2721a427e961b9fd6a194c3888a180de9990b43f8530a02bd6be017e17`.
No plugins are added. Refresh either image only in a reviewed new bundle/source
revision after live version/security review and this integration qualification.
Never change PostgreSQL major through an image refresh; PostGIS upgrades also need
an explicit tested extension step. Do not rebuild an existing stable release.

## Configuration and protected files

For guided installations, the
[persisted lifecycle authority contract](25-Container-Release-Contract.md#persisted-compose-lifecycle-authority)
owns `installation.json`, its generated companions, receipts and generation pointers.
The following manual substrate instructions do not create operator-owned setup or
completion authority. File/container/volume existence cannot establish that authority.

Copy `config/deployment.env.example` to an absolute administrator-owned location,
for example `/etc/wayfarer/deployment.env`. It contains literal `KEY=value` entries,
no shell expansion, quoted values or passwords. Set the public DNS hostname,
application and derived DB `sha256:` digests from genuine release evidence, mode and secret paths.
The application reference becomes `ghcr.io/stef-k/wayfarer@sha256:...`.
The DB reference becomes `ghcr.io/stef-k/wayfarer-db@sha256:...`; Caddy is pinned directly in Compose.
Use validated release metadata for those identities. Ordinary guided setup generates
these inputs through [the operator](29-Wayfarerctl.md#fresh-guided-setup).

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
These are internal maintenance seams used by the operator; ordinary installation
uses [guided setup](29-Wayfarerctl.md#fresh-guided-setup).

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
The external-mode qualification covers a Linux host-native proxy reaching that loopback port; other
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
in ordinary lifecycle commands. Managed restore stages fresh generations; managed
updates require explicit plan authorization and general rollback remains unavailable.

CI reuses the application-image dry-run and runs
`tools/compose/qualify.py --image <local-app-image-ID> --db-image <local-db-image-ID>`. Its test-only override selects that exact local build, an isolated
project and high loopback TLS port. Full AMD64 qualification uses a fixture ACME
authority; native-only/ARM64 and the external-proxy fixture retain their internal CA.
Test configurations disable CA trust-store installation; curl trusts only the
explicitly supplied temporary root. Production pins and the managed site block
remain unchanged. It validates both config modes, malformed inputs,
fresh non-superuser migration/seed/bootstrap, health, real page/static/KML/SSE/PDF/
thumbnail paths, network/mount boundaries, DB/key/upload/TLS state after recreation,
logical dump/restore, authenticated-cookie survival, public client-IP spoof resistance
and a real separate host-native Caddy proxy through the external loopback endpoint. It deletes only its random
project-labelled test resources and temporary secret files, never global Docker state.
CI builds the DB from its pinned upstream base/packages and checks actual DB versions;
Caddy is pulled by its production digest. Registry acceptance of the derived DB remains separate.
Existing image/browser/release and ordinary application CI remain separate gates.
This is disposable integration evidence, not public-CA issuance, production host,
native migration, arbitrary external-proxy topologies or physical mobile devices.
The bounded AMD64 API/embed/live joins are described below.

<!-- Preserve the existing inbound anchor used by other documentation. -->
<a id="managed-acme-and-timeline-ingress-749"></a>

### Managed ACME and Timeline ingress

Full AMD64 qualification has passed for the shipped Caddy 2.11.4 automatic ACME
client against a local fixture ACME authority. Only the test authority/root, renewal
timing and network/port plumbing differ from production. The client uses neither
`tls internal` nor a manually loaded leaf. Curl/Python validate HTTPS live/ready and
the hostname SAN against only that fixture root.

| Observation | Qualified behavior |
| --- | --- |
| Initial automatic issuance | A valid hostname certificate is persisted and served through managed HTTPS |
| Caddy-only replacement | Account/certificate hashes, served leaf and `caddy-data`/`caddy-config` identities survive; app/DB containers remain unchanged |
| Automatic renewal within 360 seconds | A new persisted/served leaf uses the same account and volumes |
| Bearer check-ins through managed TLS | Two distinct Locations persist for the same synthetic token owner |
| Mounted Timeline embed | The cross-origin public Timeline iframe displays Leaflet and the canonical full-view link |
| Product tile route | A Wayfarer tile request succeeds through ingress; upstream availability/retry matrices remain lower-seam tests |
| Production EventSource | Two connections on the same mounted iframe join real check-in messages to Timeline refreshes, including after replacement/reconnect |

The full run also covers rendering, upload, forwarding/exposure,
DB/key/upload/cookie/TLS persistence, logical restore and the external host-native
proxy. Evidence records bounded hashes, certificate facts, fingerprints, volume
identities, statuses and event/refresh joins without token/password/cookie/private-key
bytes. Cleanup removes only the labelled installation and its secrets.

This is Compose evidence for actual ACME automation against a **local test CA**.
Public AMD64/ARM64 setup acceptance and the genuine **v1.9.21 → v1.9.22** continuous
AMD64 lifecycle have passed separately; see
[public distribution acceptance](27-Application-Image-Publication.md#stable-compose-distribution)
and the [continuous lifecycle boundary](27-Application-Image-Publication.md#post-publication-continuous-lifecycle).
Public DNS/CA reachability and production-host acceptance remain installation-specific.
The full browser/ACME journey is AMD64-only and does not establish physical-device support.

## Derived DB publication and recovery

The bounded `.github/workflows/database-image.yml` manual workflow publishes only
`ghcr.io/stef-k/wayfarer-db`. It does not create an application release, Git tag,
release tarball or `release.json`. Select an independently reviewed full source SHA
containing this workflow, recipe and qualification tools. The selected workflow ref
must resolve to exactly the supplied `source`; checkout stays at that SHA.

The immutable publication tag is
`pg18.6-postgis3.6.4-bookworm-<full-source-SHA>`. It identifies a reviewed DB assembly,
independently of the application release number. Later complete release bundles must
consume its qualified **registry manifest digest** and retained evidence, never its
mutable tag or local image ID. A refreshed recipe requires a new reviewed source and
publication identity. Existing identities are never overwritten, including reruns.

Stable application/bundle tooling consumes
[`tools/release/database-release.json`](../tools/release/database-release.json).
Publishing a DB image does not promote that accepted authority; use the separate
[evidence-promotion PR](#accepted-db-evidence-promotion) below. Recipe changes and
accepted registry evidence remain independent. Immutable validation, supported
platform selection and candidate/stable bundle behavior are owned by
[the release contract](25-Container-Release-Contract.md#local-release-authority-v1).

The publisher builds the shared pinned recipe and verifies actual Debian package and
PostgreSQL executable versions against OCI metadata. AMD64 runs the full disposable
Compose gate (including executable PostGIS SQL, locale/citext and dump/restore); ARM64
runs the bounded fresh-cluster and thumbnail/PDF rendering gate before push.
Native publication and index assembly receive `packages: write`, using `GITHUB_TOKEN` over
stdin. No PAT is needed. Native AMD64 and ARM64 jobs each bind a tested manifest/config;
the existing manual workflow joins them into one immutable DB index only after both
anonymous qualification jobs pass. OCI and
JSON evidence include source, exact package versions and official base reference.
The workflow serializes DB writes and requires explicit registry absence before build
and immediately before push. Authentication/network/ambiguous errors fail closed.
GHCR has no conditional create-only push: other package writers must not race this
workflow, retarget tags or delete immutable evidence.

`db-publication-amd64` / `db-publication-arm64` JSON is uploaded before the anonymous job starts, even if a
post-push registry check fails. A fresh runner with read-only repository permission
uses an empty Docker client configuration to pull only the captured digest. It checks
platform, OCI/package/executable identity, then repeats its native Compose gate with
that pulled DB reference and `pull_policy: never`. The DB is never rebuilt in this
job. The application companion is built locally from the same source in each job.
Only successful `db-evidence-{amd64,arm64}` records native anonymous qualification.
Preserve both publication and qualification artifacts with the index evidence below.

### Maintainer activation and exact-source dispatch

[GitHub requires a manual workflow on the default branch before dispatch](https://docs.github.com/en/actions/how-tos/manage-workflow-runs/manually-run-a-workflow).
The workflow is available there. After recipe review, merge and explicit maintainer
publication authorization, dispatch a ref resolving to the exact accepted full source
SHA. The workflow requires that SHA to equal its selected ref; verify it has not moved.
Local/PR dry runs do not authorize or prove registry publication.

```sh
gh workflow run database-image.yml --ref REVIEWED_REF \
  -f source=FULL_REVIEWED_SHA -f db_version=pg18.6-postgis3.6.4-bookworm
gh run list --workflow database-image.yml
gh run watch RUN_ID --exit-status
gh run download RUN_ID -n db-publication-amd64 -D /absolute/evidence/publication-amd64
gh run download RUN_ID -n db-publication-arm64 -D /absolute/evidence/publication-arm64
gh run download RUN_ID -n db-evidence-amd64 -D /absolute/evidence/qualified-amd64
gh run download RUN_ID -n db-evidence-arm64 -D /absolute/evidence/qualified-arm64
gh run download RUN_ID -n db-index -D /absolute/evidence/index
```

If the new GHCR package is private, preserve `manifestDigest` from that artifact,
then set the **wayfarer-db** package visibility to **public** in GitHub package
settings. [Public GHCR packages support anonymous pulls](https://docs.github.com/en/packages/learn-github-packages/configuring-a-packages-access-control-and-visibility).
Rerun only failed anonymous jobs (`gh run rerun RUN_ID --failed` when only those
jobs failed). If a recorded two-platform index already exists, a separate recovery
dispatch can qualify its native selections without any publication job:

```sh
gh workflow run database-image.yml --ref REVIEWED_REF \
  -f source=FULL_REVIEWED_SHA -f db_version=pg18.6-postgis3.6.4-bookworm \
  -f digest=sha256:RECORDED_TWO_PLATFORM_INDEX
gh run watch RECOVERY_RUN_ID --exit-status
gh run download RECOVERY_RUN_ID -n db-evidence-amd64 -D /absolute/evidence/recovered-amd64
gh run download RECOVERY_RUN_ID -n db-evidence-arm64 -D /absolute/evidence/recovered-arm64
```

The recovery input must resolve both recorded native platforms; a lone native
manifest cannot qualify both runners. Recovery dispatch skips index publication
as well as native publication. If no index exists yet, recover the original run's
anonymous jobs from their retained native artifacts, then complete its index job.
Never rerun the publishing job after a push, including interrupted runs. If digest
recording was interrupted, inspect registry/run evidence; do not rebuild or delete
the existing tag. Authentication failure during the absence check also requires a
maintainer access investigation, not relaxed absence checks. A successful public
pull and pulled-artifact native qualification remain mandatory before promotion.

PR CI runs `tools/release/db_image.py dry-run` against a clean checkout with the same
build/metadata/full Compose path, without login, package-write permission or push.
Focused publication tests run with the existing `tools/release/tests` selection.

### Accepted DB evidence promotion

After successful native publication, anonymous qualification and index assembly,
preserve all five artifacts above beyond Actions retention. Independently review
`db-index.json` against the exact recipe source, package/base-image facts and native
qualification evidence. Promotion is a separate reviewed source change:

```sh
gh run download RUN_ID -n db-index -D /absolute/new-evidence-directory
cp /absolute/new-evidence-directory/db-index.json tools/release/database-release.json
```

Copy the actual artifact unchanged; do not reconstruct a subset or select by a mutable
tag. Validate and independently review the promotion PR under repository rules before
merge.
Ordinary application releases require no DB metadata/source change while this accepted
baseline is unchanged. Application publication verifies it before pushing; canonical
bundles retain their own exact native DB identity. Promotion does not update installations
or authorize a DB-major/extension upgrade. Continue through
[release orchestration](23-Versioning.md#choose-the-release-path).

## SSE proxy qualification

The managed Caddyfile and external proxy fixture omit `flush_interval -1`.
[Caddy's streaming documentation](https://caddyserver.com/docs/caddyfile/directives/reverse_proxy#streaming)
describes automatic immediate flushing for `text/event-stream`; it also describes negative
flush intervals as preventing upstream cancellation on early downstream disconnect.
The default streaming behavior therefore supplies the required flush contract without
that override. Local probes of pinned Caddy 2.11.4 observed prompt cancellation both
with and without the override; they do not establish a cancellation defect in that version.

Run `python3 tools/compose/qualify_sse.py` on Linux with Docker and .NET 10. The disposable
MVC host compiles the actual transport source. The probe reads the production Caddyfile
and pinned image, substituting only loopback site/upstream addresses. It checks first event,
the real 20-second heartbeat, downstream close → action `RequestAborted` → zero active
connections/channels, and a finite JSON response. Only after that retirement, a new
subscription with a distinct identity reconnects through the same running Caddy and
upstream process, receives the expected event, then closes to final zero clients/channels.
The second subscription does not repeat the heartbeat wait. Output records subscription
identities, event/heartbeat/cleanup timings, cancellation state and unchanged process/container
identity. It removes its own container/temp files.

The transport probe covers loopback HTTP/1.1, not performance percentiles or deployed
HTTPS/HTTP2/device qualification. Managed HTTPS and mounted browser reconnect are
covered by the [managed ingress qualifier](#managed-acme-and-timeline-ingress).

Mobile source/test compatibility evidence covers remote EOF and non-cancelled I/O
reconnect while local Stop remains terminal. HTTP 401/403/404 remain terminal and
existing 429/503 backoff remains. This is not physical-device qualification. The
same-proxy reconnect probe supplies server/proxy transport evidence and does not replace
those mobile tests.

## Retained local release bundles

Validated offline bundles install immutably beneath the explicit deployment root's
`releases/` directory. Candidate names cannot collide with stable `vX.Y.Z` names.
Import uses protected staging and no-replacement publication; it does not select a
new application release, run migration or recreate containers. Explicit adoption adds
schema-4 installation release authority while preserving runtime inputs and local
policy. Every subsequent installation load validates retained bytes and matching
runtime inputs. Current/previous bundles and images have no automatic garbage collection.
See [operator adoption and dispatch](29-Wayfarerctl.md#immutable-local-release-bundles).


## Native platform selection

`wayfarerctl` derives `WAYFARER_PLATFORM` from the supported native host and selected
release. It is not an arbitrary configuration option. One Compose template applies
that validated platform to app, DB and Caddy; recovery/update helpers use the same
release platform. Each canonical bundle pins the executable DB platform manifest,
while app release metadata distinguishes its shared index from the selected manifest.
The DB package/executable, Debian/glibc, locale, volumes and non-superuser contracts
remain identical. Stable application publication validates the committed accepted
DB evidence and both native selections before building or pushing any application
manifest. See [derived DB publication and recovery](#derived-db-publication-and-recovery)
for the separate reviewed evidence-promotion contract.

Candidate/stable DB selection and the historical serialization fallback are described
in [the release contract](25-Container-Release-Contract.md#local-release-authority-v1).
