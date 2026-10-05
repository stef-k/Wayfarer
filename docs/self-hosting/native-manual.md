---
title: Advanced native and manual deployment
---

# Advanced native and manual deployment

This page is for administrators who deliberately run Wayfarer directly on the host
instead of using the supported Compose/`wayfarerctl` production path.

Native deployment gives you full control over systemd, PostgreSQL, Nginx and
browser/runtime provisioning, but you also own their compatibility and recovery.
For a new production installation, prefer [guided self-hosting](index.md).

**Automated migration from an existing native/systemd installation to Compose is
not currently supported.** Do not treat `wayfarerctl update` as a native migration
tool.

## Native deployment responsibilities

You own:

- the .NET runtime/published application;
- Node/npm on the **build host** for the Trip Editor;
- PostgreSQL with PostGIS;
- schema migration and reference seeding;
- the initial protected administrator bootstrap;
- systemd service identity and environment;
- reverse proxy and TLS;
- trusted forwarded-header configuration;
- writable data/cache/log/temp roots;
- version-matched Playwright Chromium and its OS libraries;
- backups of database, Uploads and the complete active Data Protection key ring.

Production web startup intentionally does **not** migrate, seed or create/reset the
administrator.

## Platform and dependencies

The current project baseline is .NET 10 and Node 24. Native/manual deployment uses
PostgreSQL 18 with PostGIS.

Ubuntu 24.04 is the primary maintained Linux baseline, but a native operator must
verify package names and runtime dependencies for the chosen distribution.

A practical native host needs:

- a dedicated service account;
- .NET 10 runtime (SDK on the build host);
- PostgreSQL + PostGIS;
- Node 24/npm on the build host;
- a reverse proxy such as Nginx;
- enough disk for application data, caches, imports, logs and backups.

## Create a service identity

Use a dedicated unprivileged account, for example:

```sh
sudo useradd -r -s /usr/sbin/nologin wayfarer
```

If you intentionally need a home directory for service-owned state, create it
explicitly and restrict it to that account.

Do not run the web process as root.

## Prepare PostgreSQL

Create a dedicated database and application role, then enable the required
extensions.

Representative SQL:

```sql
CREATE DATABASE wayfarer;
CREATE USER wayfareruser WITH PASSWORD 'replace-with-a-protected-secret';
GRANT ALL PRIVILEGES ON DATABASE wayfarer TO wayfareruser;

\c wayfarer
CREATE EXTENSION IF NOT EXISTS postgis;
CREATE EXTENSION IF NOT EXISTS citext;
GRANT ALL ON SCHEMA public TO wayfareruser;
```

Store the connection string through protected host configuration, not in committed
`appsettings*.json`.

For systemd, ASP.NET Core environment variables use double underscores:

```ini
Environment="ConnectionStrings__DefaultConnection=Host=localhost;Database=wayfarer;Username=wayfareruser;Password=..."
```

## Build and publish

From a trusted source checkout on the build host:

```sh
dotnet restore
dotnet tool restore
dotnet frontend build
npm ci
npm run build
dotnet publish Wayfarer.csproj -c Release -o /absolute/publish
```

Validate the **published** output, not only a source-tree Production run. Published
static-web assets and the Trip Editor manifest must be present.

Deploy the published tree to a root-controlled location such as
`/var/www/wayfarer`, readable by the service identity.

## Prepare runtime storage

The Linux Production defaults use:

```text
/var/lib/wayfarer
/var/cache/wayfarer
/var/log/wayfarer
/tmp/wayfarer
```

The service user needs appropriate access to the runtime-owned portions. In
particular, prepare the Uploads/import path beneath the selected DataRoot before
startup.

Prefer explicit ASP.NET Core environment variables or a protected production
configuration source when changing these roots.

### Legacy native storage

Older native installations can still contain same-host compatibility references
beneath historical `Uploads`, `TileCache` or `ImageCache` trees.

Do **not** delete those directories merely because newer writes use externalized
storage roots. Old database rows can still reference legacy upload paths and cache
state can still be retired/replaced through normal application behavior.

Cross-host/native-to-Compose conversion of those references requires deliberate
migration; startup does not rewrite them for you.

## Data Protection key ring

Personal provider credentials and other durable protected payloads require the
complete active ASP.NET Core Data Protection key ring.

Native resolution follows this order:

1. nonblank `DataProtection:KeyRingPath` / `DataProtection__KeyRingPath`;
2. otherwise `Storage:DataRoot/data-protection`;
3. if only the previous platform default contains ordinary `key-*.xml` files,
   keep using that ring in place;
4. if both distinct defaults contain keys, fail closed.

Do not merge key rings or choose one by file age.

The stable Data Protection application name is `Wayfarer`. Existing explicit
native overrides such as:

```text
/home/wayfarer/.aspnet/DataProtection-Keys
```

remain authoritative until you intentionally migrate them.

Back up PostgreSQL and the **complete resolved active key ring together**. Restore
the ring with the service user's expected ownership and restrictive permissions
before starting the application. Database-only recovery can leave stored provider
credentials permanently unreadable.


### Prepare legacy protected credentials before stable-identity activation

Older native installations can contain provider credentials protected under the
previous content-root-derived application identity. The current stable runtime
reads only the stable companion identity.

Before activating a release that expects the stable identity:

1. stop/quiesce the old service while it is still running from its original
   readable content root and key-ring configuration;
2. back up PostgreSQL and the complete resolved active key ring together;
3. apply the additive credential-companion schema migration if it is still absent;
4. from that same service account, working/content root and ring configuration, run:

```sh
dotnet Wayfarer.dll data-protection prepare-stable-identity
dotnet Wayfarer.dll data-protection status
```

Require exit 0 from `status` and **Activation-ready: True** before starting the
stable-identity web runtime. Then take another paired database + complete-ring
recovery set.

If the database was already prepared, do **not** rerun preparation blindly. With the
service stopped, run only:

```sh
dotnet Wayfarer.dll data-protection status
```

and require the intended ring plus activation-ready state.

These offline commands disable automatic key generation and do not contact
providers. Preparation is transactional and fills only missing stable companions;
it fails closed on unreadable, mismatched or inconsistent credential state.

If the old content-root identity is already lost, moving the key files to a new
root does not recreate it. Restore the readable source identity or re-enter the
affected credentials deliberately.

After stable-identity credential replacement or revocation, rollback to an older
legacy reader may no longer be able to read those credentials. Recovery then needs
the matching pre-change database + complete-ring set or credential re-entry.

## Run maintenance before Production startup

From the published directory, under the same service identity/configuration used
by the web process:

```sh
dotnet Wayfarer.dll database migrate
dotnet Wayfarer.dll database seed
dotnet Wayfarer.dll admin bootstrap <username> --stdin < /protected/password-file
```

For an existing administrator whose password must be recovered:

```sh
dotnet Wayfarer.dll admin reset <username> --stdin < /protected/password-file
```

Use protected stdin, not a password in argv or shell history.

The Production password policy requires at least 15 characters with uppercase,
lowercase, a digit and a non-alphanumeric character.

Run the web service only after migrations/seeding/bootstrap complete successfully.

## Provision Playwright Chromium

PDF generation uses the Chromium version matched to Wayfarer's
Microsoft.Playwright package.

The application does **not** download or install Chromium during startup or a PDF
request.

After publishing, use that release's generated Playwright installer. Example:

```sh
sudo pwsh /var/www/wayfarer/playwright.ps1 install-deps chromium
sudo env PLAYWRIGHT_BROWSERS_PATH=/opt/wayfarer-browsers \
  pwsh /var/www/wayfarer/playwright.ps1 install chromium
```

Give the service read/execute access to the browser bundle and configure:

```ini
Environment=PLAYWRIGHT_BROWSERS_PATH=/opt/wayfarer-browsers
```

Do not substitute an arbitrary system Chromium when the release expects a
version-matched Playwright bundle.

Keep temporary browser profiles outside the publish tree.

## Configure systemd

Representative service:

```ini
[Unit]
Description=Wayfarer
After=network.target postgresql.service

[Service]
User=wayfarer
WorkingDirectory=/var/www/wayfarer
ExecStart=/usr/bin/dotnet /var/www/wayfarer/Wayfarer.dll --urls http://127.0.0.1:5000
Restart=always
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=PLAYWRIGHT_BROWSERS_PATH=/opt/wayfarer-browsers
Environment="ConnectionStrings__DefaultConnection=Host=localhost;Database=wayfarer;Username=wayfareruser;Password=..."
Environment="TrustedProxy__Addresses__0=127.0.0.1"
Environment="AllowedHosts=wayfarer.example.com"
Environment="Application__ContactEmail=admin@example.com"

[Install]
WantedBy=multi-user.target
```

If the reverse proxy also connects over `::1`, add the corresponding trusted
proxy entry.

`AllowedHosts` should contain semicolon-separated exact public DNS hostnames.
Do not use URL schemes, ports, wildcard hosts, localhost/private names or IP
literals as public origin identities.

After editing the unit:

```sh
sudo systemctl daemon-reload
sudo systemctl enable wayfarer
sudo systemctl start wayfarer
```

## Configure Nginx or another reverse proxy

The proxy should terminate HTTPS and forward to the local Kestrel endpoint.

Minimal Nginx shape:

```nginx
server {
    listen 443 ssl http2;
    server_name wayfarer.example.com;

    client_max_body_size 100M;

    location / {
        proxy_pass http://127.0.0.1:5000;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header X-Forwarded-Proto $scheme;

        proxy_buffering off;
        proxy_cache off;
        proxy_read_timeout 3600s;
    }
}
```

Use your normal certificate automation.

Wayfarer owns application browser security headers. Do not add a blanket frame
policy at the proxy that breaks intentional public Timeline/Trip embeds.

If you expose direct thumbnail routes through Nginx, keep the existing
`/thumbs/` proxy ordering ahead of generic image rules so Kestrel remains the
authority for those responses.

## First Production start

After maintenance commands and proxy configuration:

```sh
sudo systemctl start wayfarer
sudo systemctl status wayfarer
sudo journalctl -u wayfarer -n 100
```

Open the public HTTPS URL, sign in with the protected administrator created during
bootstrap and enable 2FA.

Keep registration closed unless you intentionally operate an open instance.

## Updating a native installation

Native updates are administrator-owned.

Before an update:

1. stop Wayfarer writers;
2. take a database backup;
3. preserve Uploads;
4. preserve the complete resolved active Data Protection key ring;
5. retain the previous release/browser bundle until rollback compatibility is
   understood.

Build/publish the target release separately. Apply its pending migrations while the
old web process is stopped, then start the target only after migration succeeds.

A safe outline is:

```sh
sudo systemctl stop wayfarer

# Build/publish target into a staging directory first.
# Run the target's maintenance command with Production configuration.
sudo -u wayfarer dotnet /absolute/staged/Wayfarer.dll database migrate

# Deploy the already-built output while preserving external durable storage and
# any retained legacy compatibility directories still referenced by this install.
sudo rsync -a --delete \
  --exclude 'Uploads' --exclude 'TileCache' --exclude 'ImageCache' \
  /absolute/staged/ /var/www/wayfarer/

# Apply the ownership/read permissions required by your native service layout.
# Keep any retained legacy Uploads/TileCache/ImageCache trees writable by the
# service account while they remain referenced.

sudo systemctl start wayfarer
sudo systemctl status wayfarer
```

Adjust ownership/read permissions so the service can execute/read the published
tree but cannot rewrite deployment binaries.

Do not run an old application against a database after an incompatible migration
merely because the old files still exist. Database rollback requires a compatible
verified backup, not just a previous binary.

## Native backup boundary

At minimum, keep a recovery set containing:

- PostgreSQL dump/backup;
- complete active Data Protection ring;
- durable Uploads and import files;
- enough protected deployment configuration to reconstruct the service.

Capture these with writers stopped when you need a strong cross-component recovery
point.

Caches, thumbnails, logs, browser temp and TLS certificates are not replacements
for durable application data.

## Native troubleshooting

Application:

```sh
sudo systemctl status wayfarer
sudo journalctl -u wayfarer -n 100
```

Database:

```sh
sudo systemctl status postgresql
psql -h localhost -U wayfareruser -d wayfarer
```

Proxy:

```sh
sudo nginx -t
sudo tail -n 100 /var/log/nginx/error.log
```

Browser/PDF:

- verify `PLAYWRIGHT_BROWSERS_PATH`;
- verify the release-matched Chromium exists;
- run `ldd` on the executable Playwright reports when diagnosing missing shared
  libraries;
- use the release's generated `playwright.ps1 install-deps chromium` to correct
  OS dependencies.

Do not delete a shared browser bundle or Data Protection ring as a generic
troubleshooting step.

## Native-to-Compose boundary

The supported Compose operator does not import a native installation in place and
`wayfarerctl update` does not convert native state.

A future migration must deliberately account for:

- PostgreSQL data/version compatibility;
- Uploads and any legacy absolute upload references;
- complete Data Protection authority;
- application configuration and public ingress;
- service downtime and writer fencing;
- backup/rollback evidence.

Until such a migration path is explicitly supported, keep native and Compose
procedures separate.

[Self-hosting guide](index.md) · [Operations](operations.md) ·
[wayfarerctl reference](wayfarerctl.md)
