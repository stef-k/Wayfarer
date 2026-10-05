---
title: Application container
---

# Application container

This page owns the Wayfarer application image build/runtime contract, explicit
maintenance commands, health/configuration boundaries, Chromium packaging and
application-image qualification. Immutable bundle/image identity belongs to the
[Release contract](release-contract.md); stable publication belongs to
[Publication](publication.md).

## Image build and runtime

The root Dockerfile builds native Linux AMD64 and ARM64 application payloads.

The maintained image family is:

- Node 24 for frontend build input;
- .NET 10 SDK on Ubuntu Noble for compilation/publish;
- .NET 10 ASP.NET runtime on Ubuntu Noble for the final image;
- framework-dependent Release publish using `linux-x64` or `linux-arm64`;
- release-matched Playwright driver and Chromium installed during image build.

The final image contains the published application, generated frontend assets,
migrations, embedded Quartz SQL and the private Playwright driver/browser runtime.
It does not retain the .NET SDK, a general-purpose Node/npm installation or
PowerShell.

Runtime uses the image's non-root `app` identity (UID/GID 1654), internal HTTP
port 8080 and an exec-form `dotnet Wayfarer.dll` entrypoint. `/app` and
`/opt/wayfarer-browsers` are immutable. Writable application data, cache, logs and
temporary work are supplied through the explicit release-contract mounts.

The supported Compose runtime uses a read-only root, no Docker socket, no privilege
escalation and bounded temporary storage. Graceful shutdown gives ASP.NET/Quartz
60 seconds; Compose allows 70 seconds before forced termination.

## Explicit maintenance

The published application executable owns maintenance behavior:

~~~text
database migrate
database seed
admin bootstrap <username> --stdin
admin reset <username> --stdin
user find <username>
healthcheck
~~~

`database migrate` requires an already provisioned PostgreSQL 18 database with
PostGIS and `citext`. It applies EF migrations and aligns the embedded Quartz schema.
Unknown newer migration history is rejected. The application database role owns its
schema and does not need superuser authority.

`database seed` is explicit, idempotent reference/role/settings seeding. It does not
create an administrator. Administrator bootstrap/reset uses protected stdin and the
Production Identity policy; credentials are not passed in argv.

Production web startup validates compatible database/schema/extensions, durable
storage/Data Protection authority and secured administrator state. It does not
migrate, seed or create/reset the administrator. Development startup is intentionally
more convenient and is not a Production lifecycle substitute.

## Configuration and health

`Database__PasswordFile` is the optional password-file seam for the database
connection. It replaces only the password in
`ConnectionStrings__DefaultConnection`. Missing, unreadable or empty secret files
fail without exposing their contents.

Forwarded headers are trusted only from explicitly configured addresses/networks.
No configured trusted proxy means forwarding is disabled. Production ingress and
host selection belong to [Production Compose](production-compose.md).

Health surfaces are intentionally bounded:

- `/health/live` proves cheap process/HTTP liveness;
- `/health/ready` proves the bounded database/schema/bootstrap readiness contract;
- `healthcheck` makes the fixed loopback readiness request used by Docker.

Health does not invoke external providers or Chromium.

## Chromium and isolation

The image installs the Chromium revision required by the published Playwright
version under `/opt/wayfarer-browsers`; runtime never downloads a browser or falls
back to an arbitrary system Chromium.

Wayfarer runs Chromium as the non-root application user inside the container's
read-only and capability-restricted boundary. The current Playwright launch does not
enable the Chromium renderer sandbox, so browser isolation relies on the application
egress/content controls plus the container boundary. Privileged mode, `SYS_ADMIN`
and host IPC are outside the supported runtime contract.

A Playwright package/revision change requires rebuilding and requalifying the image;
derive the required browser revision from the shipped Playwright metadata rather than
hard-coding a different browser.

## Disposable application-image qualification

Application-image qualification uses disposable PostgreSQL **18** with PostGIS and
`citext`, never production data. It exercises migration, seed and protected
administrator bootstrap as separate maintenance invocations before web startup.

Qualification should prove the image-level contract, including:

- non-root UID/GID and immutable application/browser payload;
- absence of SDK/general Node/npm/PowerShell in the final runtime;
- generated static/frontend assets;
- external durable key-ring/cache state and write denial outside writable roots;
- live/readiness/Docker-health behavior, including bounded database failure/recovery;
- an ordinary page plus real MapSnapshot/thumbnail/PDF/PNG Chromium paths;
- sanitized logs/secret absence;
- graceful SIGTERM and Quartz shutdown.

This is application-image evidence. It does not by itself qualify the production
Compose database image, public registry distribution or a production host.

## Offline bundle image qualification

A local release bundle binds immutable application, database and Caddy identities
plus the selected native platform. Offline inspection verifies the locally available
application image's OCI source/version, compiled application version, schema/resource
contract and bundled operator/recovery protocol without network access.

Missing required local images prevent execution-ready status; validation never pulls
a replacement implicitly. The exact bundle semantics are defined by the
[Release contract](release-contract.md).
