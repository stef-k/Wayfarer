# Install & Self-Hosting

Wayfarer's recommended production path is guided Docker/Compose setup through
`wayfarerctl`: one small bootstrap archive and one setup command. The operator manages
the bundled PostgreSQL/PostGIS database, application and managed Caddy HTTPS proxy.
The Compose baseline is PostgreSQL 18.6 + PostGIS 3.6.4 on Debian Bookworm.
Native/manual and development database requirements are unchanged.

## Availability

v1.9.20 is the first published stable release with AMD64/ARM64 Compose bootstrap and
deployment assets. Its immutable installer predates the automatic retry and stage
diagnostics described below; those improvements require a later stable patch release.
Public acceptance of the corrected installer remains pending on both architectures.
Source-only v1.9.19 and earlier are not Compose installation sources. Maintainers start at
[Versioning and Release Operations](23-Versioning.md#public-acceptance-and-availability).

## Guided production installation

Prepare:

- Linux AMD64 or ARM64 with normal Unix file ownership/modes; 32-bit ARM is unsupported.
- Docker Engine using its local daemon and Compose v2 2.24.4 or newer.
- Root/sudo access and space for exact images, retained releases and durable volumes.
- A public DNS hostname. For managed HTTPS, point it at the host and make ports
  80/TCP, 443/TCP and 443/UDP available. An existing proxy can use external mode.
- Outbound HTTPS to public GitHub/GHCR and the pinned Caddy registry.

Obtain `wayfarerctl-linux-amd64.tar.gz` (AMD64) or `wayfarerctl-linux-arm64.tar.gz`
(ARM64) from the official stable
[Wayfarer Release](https://github.com/stef-k/Wayfarer/releases). Verify its SHA-256
against that exact Release asset's REST `digest` before extracting or running it.
The matching `.sha256` sidecar is a convenience integrity check, not publisher
authentication. See the [operator guide](29-Wayfarerctl.md#placement-and-prerequisites)
for the verification and protected placement details.

In a trusted fresh directory (AMD64 example; substitute the ARM64 archive on ARM64):

```sh
tar -xzf wayfarerctl-linux-amd64.tar.gz
chmod +x wayfarerctl
sudo ./wayfarerctl setup
```

The archive contains exactly `wayfarerctl`. Bare `setup` resolves the latest official
stable release for this platform, downloads and checks its exact deployment asset,
imports a validated retained bundle, and pulls/verifies only immutable image digests.
It then asks for your hostname, proxy mode and a hidden, confirmed administrator
password before running the existing protected setup sequence. Image identities come
from validated `release.json`; you do not copy digests or locate a bundle manually.
This is guided setup and requires administrator input.

The production host needs no repository clone, .NET SDK/runtime, Node/npm, Python,
`unzip`, native PostgreSQL, Nginx or Certbot installation. The operator does not install
host packages or configure the Docker daemon. Docker Desktop, remote contexts and
rootless Docker are outside the accepted production topology.

Setup creates the protected `admin` account with your chosen password. Ordinary web
startup does not migrate, seed or create an administrator; no `admin/Admin1!` default
is accepted. After setup, sign in, review **Admin > Settings**, keep registration
closed unless deliberately enabled, and enable account two-factor authentication.

Installation state defaults to `/etc/wayfarer`. Preserve its secrets, retained release
bytes and durable volumes. Keep the bootstrap at a fixed root-owned path; after setup,
use `wayfarerctl dispatch COMMAND` to run the exact retained operator, including after
updates. See [operations and recovery](29-Wayfarerctl.md) and the
[Compose topology](28-Production-Compose.md).

## Exact versions and explicit local bundles

To install one exact public stable release through the same acquisition path:

```sh
sudo ./wayfarerctl setup --version X.Y.Z
```

For controlled staging, offline installation, recovery or troubleshooting, use a
trusted, complete canonical local bundle instead:

```sh
sudo ./wayfarerctl setup --bundle /absolute/trusted/bundle
```

`--version` and `--bundle` are mutually exclusive. Local setup validates the platform
and `release.json`, derives the same immutable identities, requires the exact images
already available and verified locally, and enters the same setup engine. It skips
network release discovery/download. The full deployment archive and its checksum
remain a secondary distribution path; see the [shipped instructions](../tools/release/INSTALL.md).

Setup shows understandable progress and automatically retries temporary download
failures at most three times. If it stops with **"Setup has not started"**, correct
the reported cause and run the same plain `sudo ./wayfarerctl setup` command again,
including any original options. Verified downloads may be retained; no installation
configuration or application data was changed. Do not delete files or run `doctor`
or `setup --resume` at this boundary.

If it says **"Setup has started"**, preserve the installation files and service data,
correct the cause, then run `sudo ./wayfarerctl setup --resume` with the original
deployment-root option. Resume verifies the original protected receipt, configuration
and secrets. Integrity/safety failures stop immediately: do not bypass the check;
try again later from the official release. Missing assets never trigger a search for
another release. See [fresh-install troubleshooting](29-Wayfarerctl.md#interrupted-setup-and-troubleshooting)
for the same retry-versus-resume contract and prerequisites.

## Development and advanced native/manual installation

Source builds are a development or advanced manual path. Their dependencies are
.NET 10 SDK, Node 24 LTS/npm and PostgreSQL with PostGIS. The general native runtime
minimum remains PostgreSQL 13+; maintainer development and relational tests use
PostgreSQL 17. Ubuntu 24.04 on Linux/WSL2 is the primary development baseline; keep
WSL checkouts on the Linux filesystem. Windows remains an alternative development
path. See [development setup](14-Setup.md) for connection strings, frontend builds
and local application startup.

Native/manual production requires explicit migrations, reference seeding and protected
administrator bootstrap before web startup. It also requires an administrator-managed
proxy/TLS configuration, service identity, storage permissions and preinstalled,
release-matched Playwright Chromium/runtime libraries. Browser operations do not
download or install Chromium. See the [advanced deployment guide](20-Deployment.md)
for the maintenance sequence and systemd/Nginx material. This is separate from the
guided Compose installation; native-to-Compose migration is not implemented here.

## Configuration and ongoing operation

Use the [configuration reference](16-Configuration.md) for storage, AllowedHosts,
trusted proxies and development/native connection strings. Placeholder database
passwords in `appsettings*.json` must be replaced through protected configuration on
those manual paths. Guided Compose setup creates distinct protected DB credentials
and consumer-specific secret files; changing them is not database password rotation.

Use the [operator guide](29-Wayfarerctl.md) for status/doctor, lifecycle, protected user
recovery, opt-in recovery sets and managed restore/update. `update --plan` can acquire
the latest stable target, but execution still requires the explicit plan hash. The
first Compose stable is a fresh-install baseline, not an upgrade from source-only
history. Retain DB data and the complete application volume, including uploads and
Data Protection keys, together; caches are rebuildable and not recovery substitutes.

For help, see [troubleshooting](09-Troubleshooting.md) or open a
[GitHub issue](https://github.com/stef-k/Wayfarer/issues).
