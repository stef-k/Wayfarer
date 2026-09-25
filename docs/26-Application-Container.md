# Application container and maintenance

Implements the application side of [the accepted contract](25-Container-Release-Contract.md)
for #640. [Stable application-image publication](27-Application-Image-Publication.md)
is implemented separately by #642. The full Compose distribution and host lifecycle
CLI remain unavailable. Only Linux AMD64 is qualified.

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
Native operators must now explicitly migrate, seed and secure bootstrap before restart.
No new EF migration or automatic native-state conversion is introduced.

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
headers are ignored. Set `AllowedHosts` to the actual public hostname. Proxy topology,
TLS and Caddy remain later work.

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
It publishes framework-dependent Release linux-x64 with frontend assets, compiled Razor,
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

## Disposable qualification

Use an isolated PostgreSQL database and non-superuser application owner; provision
extensions administratively. Never reuse production/native data. Run migration, seed
and protected bootstrap as separate containers with the same state/secret mounts.
Then start the image read-only, await Docker health and request an ordinary/static URL.
Exercise a public trip MapSnapshot and a real browser PDF/screenshot; verify cached
thumbnail output under the cache root and keys under the data root. Check UID, absence
of build tools and write denial on app/browser payloads. Stop with SIGTERM and inspect
exit status and Quartz completion. Remove only the owned containers/database/state.

Recheck `postgis/postgis:17-3.5` before every qualification. On 2026-09-25 it still
resolved to the research-only old PostgreSQL 17.5 digest recorded in #638. It is not
approved for production. The maintained isolated host test database is PostgreSQL
17.11/PostGIS 3.6.4; evidence using that fallback does not qualify the production 3.5
image family. Final database-image selection remains with the Compose/release child.

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
future production topology. Repeat with `database seed`; use `-i` and protected stdin
for bootstrap. Omit the command for web startup. The environment file contains a
password-free connection string, `Database__PasswordFile=/run/secrets/database-password`
and a specific `AllowedHosts`. Healthcheck sends that allowed Host over loopback.

### Recorded local qualification

On 2026-09-25, Linux/WSL Docker Engine 29.1.3 built the real AMD64 image with .NET 10
Noble and release-matched Chromium 151.0.7922.34/revision 1234. The isolated PG17.11 /
PostGIS 3.6.4 fallback used a non-superuser application owner. Explicit migration,
idempotent seed and protected bootstrap succeeded. Production rejection of unprepared
state and nonmutating incompatible-schema checks passed at the executable/relational seam.

Read-only-root image qualification passed: UID 1654, immutable app/browser payload,
no SDK/general Node/npm/PowerShell, generated static assets, external key ring/cache,
ordinary/static HTTP, real application MapSnapshot JPEG and browser PDF/PNG, readiness
and Docker healthy. DB login failure returned sanitized 503 while live remained 200;
healthcheck returned 1 and recovered to 0. The layer diff contained only Docker-created secret mountpoints (no application writes), and the DB
secret was absent from image history/config and application logs. SIGTERM completed
with exit 0 and Quartz's shutdown-complete message within the 60-second grace.

Regression evidence: 3,293 ordinary PostgreSQL-attached tests, two existing browser
rendering tests, one published read-only runtime test, Release image build/publish and
frontend built-asset smoke passed. Browser host qualification required the Noble ALSA
library and explicit browser-cache path; the image installs its own dependencies.
This is disposable development evidence, not production-host or Compose qualification.

The production Compose substrate is now described in [Compose deployment](28-Production-Compose.md);
its managed/external topology does not complete the later guided lifecycle product.
