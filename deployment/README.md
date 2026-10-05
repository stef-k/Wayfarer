# Native deployment helpers

This directory contains the scripts and templates for Wayfarer's advanced native
systemd/Nginx deployment path. The supported default production path is Docker
Compose with `wayfarerctl`; start with the
[Self-hosting guide](../docs/self-hosting/index.md).

For native installation, upgrade, storage, Data Protection, browser and recovery
requirements, use the
[Native/manual deployment guide](../docs/self-hosting/native-manual.md). This file
only documents behavior unique to the scripts in this directory.

## Maintained native prerequisites

The maintained native baseline is:

- .NET 10;
- Node.js 24/npm on the build host;
- PostgreSQL 18 with PostGIS and `citext`;
- a Linux systemd host and reverse proxy;
- the Playwright Chromium revision matched to the deployed Wayfarer release.

`install.sh` installs PostgreSQL/PostGIS through the host's configured apt sources.
Before using it, ensure those sources resolve the PostgreSQL packages to the maintained
major 18. The script is not a PostgreSQL-major migration tool and must not be used to
replace or reuse an incompatible physical cluster.

## Scripts

### `install.sh`

The interactive installer prepares a native host. It:

- installs/verifies PostgreSQL/PostGIS, Nginx, .NET and Node/npm build prerequisites;
- creates the configured PostgreSQL database/user and enables PostGIS/`citext`;
- creates the application service identity and deployment directory;
- prepares the durable application/cache/log paths used by the native Production
  profile;
- installs or refreshes systemd, Nginx and optional Fail2ban configuration;
- can request HTTPS through Certbot;
- can run `deploy.sh` for the initial application deployment.

The installer supports the existing environment-variable/non-interactive options in
the script. Review the script and templates before running them on a host.

### `deploy.sh`

The deployment script works from a source checkout. It:

- fetches the requested branch/tag;
- restores .NET dependencies and repository tools;
- builds the Trip Editor with `npm ci` / `npm run build`;
- publishes the ASP.NET application;
- applies EF migrations using
  `Wayfarer.Models.ApplicationDbContext`;
- stops the service, synchronizes the published tree, preserves the bounded legacy
  `Uploads`, `TileCache` and `ImageCache` trees, prepares current external
  storage roots and restarts the service.

The main overrides are:

~~~sh
APP_DIR=/absolute/source DEPLOY_DIR=/var/www/wayfarer APP_USER=wayfarer SERVICE_NAME=wayfarer REF=vX.Y.Z ./deployment/deploy.sh
~~~

Run it as the account that owns the source checkout and has the required bounded
`sudo` privileges. Do not run it as a substitute for the Compose lifecycle.

### `uninstall.sh`

The removal helper stops/disables the native service and removes the native templates
it owns. Database and certificate deletion remain explicit opt-in actions. Review its
options before use; uninstalling application files is not a backup or recovery path.

## Templates

- `wayfarer.service` provides the systemd unit shape.
- `nginx-ratelimit.conf` and `wayfarer-nginx-vhost.conf` provide the native
  Nginx rate-limit/proxy shape.
- `wayfarer-nginx.conf` and the matching filter files provide optional Fail2ban
  policy.
- `refresh-service.py` refreshes the service template while preserving an existing
  explicit Data Protection key-ring assignment.

Customize paths, service user, public host, proxy/TLS settings and protected database
configuration for the target host.

## Frontend build contract

The Trip Editor is built into `wwwroot/vite/trip-editor`; Production does not run a
Node service.

For production-like source acceptance, restore the repository tool and build both
frontend pipelines before publish:

~~~sh
dotnet tool restore
dotnet frontend build
npm ci
npm run build
dotnet publish Wayfarer.csproj -c Release -o /absolute/publish
~~~

The published output must contain the Trip Editor manifest and referenced assets.
A source-tree Production run is not equivalent to validating the published output.

## Database and application configuration

Database credentials are supplied through protected host configuration, normally the
systemd environment, rather than committed `appsettings*.json`.

ASP.NET Core nested environment keys use double underscores, for example:

~~~ini
Environment="ConnectionStrings__DefaultConnection=Host=localhost;Database=wayfarer;Username=wayfarer_user;Password=..."
Environment="AllowedHosts=wayfarer.example.com"
Environment="TrustedProxy__Addresses__0=127.0.0.1"
~~~

Keep real credentials out of the repository and shell output. The
[Native/manual guide](../docs/self-hosting/native-manual.md) owns the current
maintenance/bootstrap sequence and reverse-proxy contract.

## Storage and compatibility paths

Current native Production storage uses the configured `Storage` roots, with the
standard Linux locations under:

~~~text
/var/lib/wayfarer
/var/cache/wayfarer
/var/log/wayfarer
/tmp/wayfarer
~~~

The scripts prepare current import, tile, image, thumbnail and log locations while
retaining the bounded legacy `Uploads`, `TileCache` and `ImageCache` trees used by
older same-host references. They do not bulk-rewrite database paths or treat a cache
directory as durable backup data.

The canonical source-development storage/configuration contract is in
[Development configuration](../docs/development/configuration.md).

## Data Protection and recovery

The complete active Data Protection key ring is durable authority and must be backed
up together with PostgreSQL and durable uploads/imports. The service-template refresh
preserves an existing explicit `DataProtection__KeyRingPath` assignment rather than
silently relocating it.

Do not delete or merge key rings as a troubleshooting step. Follow the
[Native/manual deployment guide](../docs/self-hosting/native-manual.md#data-protection-key-ring)
for preparation, backup and restore requirements.

## Chromium provisioning

Browser/PDF features require the Playwright Chromium bundle matched to the deployed
Wayfarer release. Wayfarer does not install browsers during application startup.

Follow
[Provision Playwright Chromium](../docs/self-hosting/native-manual.md#provision-playwright-chromium)
before relying on browser-backed features.

## Usage examples

First-time native setup:

~~~sh
git clone https://github.com/stef-k/Wayfarer.git
cd Wayfarer
chmod +x deployment/install.sh deployment/deploy.sh deployment/uninstall.sh
./deployment/install.sh
~~~

Deploy the configured `main` branch:

~~~sh
./deployment/deploy.sh
~~~

Deploy a specific tag:

~~~sh
REF=vX.Y.Z ./deployment/deploy.sh
~~~

For routine supported Compose operation, use
[Operations](../docs/self-hosting/operations.md) instead of these native scripts.
