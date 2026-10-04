# Application container and maintenance

The application container follows [the container contract](25-Container-Release-Contract.md).
[Stable application-image publication](27-Application-Image-Publication.md)
owns image distribution separately. The shared Compose lifecycle supports Linux
AMD64 and ARM64. Maintainer sequencing starts at
[Versioning and Release Operations](23-Versioning.md).

## Explicit maintenance

Run the same published `dotnet Wayfarer.dll` executable used by the web process:

```text
database migrate
database seed
admin bootstrap <username> --stdin
admin reset <username> --stdin
user find <username>
healthcheck
```

Migrate requires an already provisioned database with `postgis` and `citext`, applies
EF migrations and installs/upgrades the embedded Quartz schema. Unknown newer EF
history is rejected. Provision extensions with a separate administrative credential;
the app/migration role needs schema rights, never superuser. Stop web/jobs and serialize
maintenance before mutation. A failed migration is not permission to downgrade.

Seed is idempotent reference/settings/role seeding, separate from migrations. It does
not create an administrator. Bootstrap creates a new active protected administrator;
existing names require explicit reset. Redirect a protected password file into stdin
or supply it from a protected pipe. Passwords are not echoed, logged or passed in argv.
The known native default password is rejected. Commands return 0 for success, 1 for
operational failure/not found, 2 for invalid syntax. User lookup emits only ID/name JSON.
The old `reset-password <username> <password>` remains deprecated native/manual
compatibility; migrate scripts to protected stdin.

Production startup checks exact EF history, Quartz compatibility, extensions, seeded
references and a secured protected administrator before activation. It validates the
existing Storage and Data Protection authority and starts jobs only after preparation.
It does not migrate, seed or create/reset an administrator. Development retains its
convenience migrations and default administrator; do not use Development in production.
Native operators must explicitly migrate, seed and secure bootstrap before restart.
Container startup does not convert native state automatically.

## Configuration and health

`Database__PasswordFile` is the single optional password-file authority. It replaces
only the password in `ConnectionStrings__DefaultConnection`. Missing, unreadable or
empty files fail without revealing their path/content. Without the setting, native
connection-string behavior is unchanged. Mount the file read-only, mode 0600, readable
by UID 1654; protect its parent directory. Terminal CR/LF is stripped from the secret.
Never put credentials in image builds, command arguments or ordinary environment files.

`TrustedProxy__Addresses__0` accepts an explicit IP; `TrustedProxy__Networks__0` accepts
a bounded CIDR. Additional indexed entries are allowed. With no entries forwarding is
disabled, including loopback. Native nginx operators must explicitly configure their
loopback peer. Exactly one trusted hop may supply scheme, host and client IP; untrusted
headers are ignored. Set `AllowedHosts` to the actual public hostname. Proxy, TLS
and Caddy topology belong to [Compose deployment](28-Production-Compose.md).

`/health/live` returns cheap HTTP liveness. `/health/ready` performs a bounded local DB
compatibility/bootstrap probe and returns only `ready` (200) or `not ready` (503).
Neither invokes providers or Chromium. `healthcheck` requests fixed loopback port 8080
with a seven-second timeout, no HTTP proxy or redirect following, and maps 200 to exit 0.
The database probe has a five-second cancellation deadline and three-second SQL timeout.

## Image build and runtime

```sh
docker build --platform linux/amd64 -t wayfarer:qualification .
```

The Dockerfile pins Noble .NET 10 build/runtime and build-only Node 24 image digests.
It publishes framework-dependent Release linux-x64 (AMD64) or linux-arm64 (ARM64) with frontend assets, compiled Razor,
docs, migrations and the embedded Quartz SQL. The published Playwright driver installs
its matching Chromium and Noble dependency set during build. The final image retains
that private driver but no general Node/npm, SDK or PowerShell.

Runtime selects `app` (1654:1654), port 8080 and an exec-form entrypoint. `/app` and
`/opt/wayfarer-browsers` are root-owned and non-writable. Supply the four writable
mounts from the normative contract: `/var/lib/wayfarer`, `/var/cache/wayfarer`,
`/var/log/wayfarer`, `/tmp/wayfarer`. Unmounted image state directories remain root-owned and non-writable to app, so missing
mounts fail instead of silently storing state in a container layer. Prepare mounted
ownership before launch; durable data/key
parents require 0700. The image does not initialize arbitrary host ownership. Preserve
the complete Data Protection ring under the durable root. Use read-only root, a private
temporary mount, no Docker socket and no privilege escalation. Allow 60 seconds for
ASP.NET/Quartz shutdown; forced termination is a failed graceful-stop observation.

## Browser isolation boundary

The supported Docker platforms are Linux AMD64 and ARM64. Release-matched Playwright Chromium
is bundled and runs within the non-root, read-only application container boundary.
With Playwright 1.63.0 launch behavior, Wayfarer does not set
`ChromiumSandbox = true`, so the effective Chromium launch includes `--no-sandbox`
even when Wayfarer supplies no such argument itself.

Browser isolation relies on application-owned egress/content/admission/cancellation
controls and container isolation; the Chromium renderer sandbox is not enabled.
Privileged mode, SYS_ADMIN and host IPC are outside the supported runtime boundary.
[Native Linux ARM64 sandbox prerequisites](https://github.com/stef-k/Wayfarer/issues/681)
still require qualification; AMD64/source evidence does not establish them or remove
the native compatibility branch. Compose uses separate native ARM64 CI
and [public distribution qualification](27-Application-Image-Publication.md#stable-compose-distribution).

## Disposable qualification

Use an isolated PostgreSQL database and non-superuser application owner; provision
extensions administratively. Never reuse production/native data. Run migration, seed
and protected bootstrap as separate containers with the same state/secret mounts.
Then start the image read-only, await Docker health and request an ordinary/static URL.
Exercise a public trip MapSnapshot and a real browser PDF/screenshot; verify cached
thumbnail output under the cache root and keys under the data root. Check UID, absence
of build tools and write denial on app/browser payloads. Stop with SIGTERM and inspect
exit status and Quartz completion. Remove only the owned containers/database/state.

Use [Compose's DB recipe and qualification owner](28-Production-Compose.md#exact-third-party-image-decision)
for current container DB selection. Application-only checks using an isolated host
PG17/PostGIS database do not qualify the Compose PG18 baseline or a DB image family.

A one-shot command uses the same mount set as the web process. For example, after
preparing the task-owned directories, the non-secret connection settings file and
UID-readable password file (substitute absolute disposable paths):

```sh
docker run --rm --read-only --network host \
  --env-file /absolute/qualification/runtime.env \
  --mount type=bind,src=/absolute/qualification/database-password,dst=/run/secrets/database-password,readonly \
  --mount type=bind,src=/absolute/qualification/data,dst=/var/lib/wayfarer \
  --mount type=bind,src=/absolute/qualification/cache,dst=/var/cache/wayfarer \
  --mount type=bind,src=/absolute/qualification/logs,dst=/var/log/wayfarer \
  --mount type=bind,src=/absolute/qualification/temp,dst=/tmp/wayfarer \
  wayfarer:qualification database migrate
```

Here host networking is a disposable test attachment to a loopback test DB, not the
production topology. Repeat with `database seed`; use `-i` and protected stdin
for bootstrap. Omit the command for web startup. The environment file contains a
password-free connection string, `Database__PasswordFile=/run/secrets/database-password`
and a specific `AllowedHosts`. Healthcheck sends that allowed Host over loopback.

### Application image evidence

Disposable AMD64 application-image qualification covers explicit migration, idempotent
seed and protected bootstrap with a non-superuser application owner. Production
rejection of unprepared state and nonmutating incompatible-schema checks are covered
at the executable/relational seam.

Read-only-root checks cover UID 1654, immutable app/browser payload,
no SDK/general Node/npm/PowerShell, generated static assets, external key ring/cache,
ordinary/static HTTP, real application MapSnapshot JPEG and browser PDF/PNG, readiness
and Docker health. DB login failure yields sanitized 503 while live remains 200;
healthcheck fails and recovers with DB access. Image-layer and log checks cover secret
absence and the writable-state boundary. SIGTERM/Quartz shutdown must complete within
the 60-second grace. This is disposable application evidence; current platform-specific
publication and Compose qualification remain separate, as does production-host acceptance.

The production Compose substrate is described in [Compose deployment](28-Production-Compose.md);
guided operation belongs to [wayfarerctl](29-Wayfarerctl.md).

## Offline bundle image qualification

Local release bundles bind immutable application/DB/Caddy digests and their selected Linux AMD64 or ARM64 platform.
The release validator independently checks local Docker identity, OCI source/version,
compiled application version, the application-owned offline schema/resource contract,
and the exact bundled operator/recovery protocol. Helpers run without network, Docker
socket, installation data or secrets. Missing images prevent execution-ready status;
no pull is attempted. See [the local release contract](25-Container-Release-Contract.md#local-release-authority-v1).
