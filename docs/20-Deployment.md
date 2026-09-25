# Deployment & Operations

The Compose operator foundation now provides secure fresh setup and routine management through
[`wayfarerctl`](29-Wayfarerctl.md). Final release packaging, backup/restore/update and native
migration remain separate #603 work; the existing native path below remains available.

This guide covers installation, deployment, logging, and operational commands for Wayfarer on Linux servers.

For the planned Docker distribution, see the normative [container and release contract](25-Container-Release-Contract.md). The [application image and explicit maintenance commands](26-Application-Container.md) are implemented; Compose/publication remain planned. This guide remains the native/manual deployment path.

Production startup no longer migrates, seeds or creates an administrator. Existing native install/deploy helpers do not replace the explicit maintenance sequence below; run it with the service identity and its protected configuration before starting/restarting. Configure `TrustedProxy__Addresses__0=127.0.0.1` (and `::1` as a second entry if used) for native loopback nginx.

---

## Table of Contents

1. [Prerequisites](#prerequisites)
2. [System User Setup](#system-user-setup)
3. [Install Dependencies](#install-dependencies)
4. [Clone the Application](#clone-the-application)
5. [Database Setup](#database-setup)
6. [Application Configuration](#application-configuration)
7. [Directory Structure & Permissions](#directory-structure--permissions)
8. [Build & Test Locally](#build--test-locally)
9. [Nginx Reverse Proxy Setup](#nginx-reverse-proxy-setup)
10. [HTTPS with Let's Encrypt](#https-with-lets-encrypt)
11. [Running as a System Service](#running-as-a-system-service)
12. [Post-Installation](#post-installation)
13. [Updating Wayfarer](#updating-wayfarer)
14. [Logging & Auditing](#logging--auditing)
15. [CLI Commands](#cli-commands)
16. [Troubleshooting](#troubleshooting)
17. [Security Hardening](#security-hardening--rate-limiting--fail2ban)
18. [Security Checklist](#security-checklist)

---

## Prerequisites

### Hardware Requirements

**Minimum:**
- A tentative 1 GB RAM minimum when the image proxy's encoded download ceiling is no greater than its 50 MiB default. Higher values in the supported 5–200 MiB range require proportionally more host memory; four 200 MiB origin downloads alone can retain approximately 800 MiB before decoded images, output, cache I/O, ASP.NET, and runtime overhead.
- **5 GB disk space** minimum:
  - ~2 GB for tile cache (zoom <= 8: ~1 GB permanent, zoom >= 9: 1 GB default, 256 MB minimum per OSM 7-day caching policy)
  - ~512 MB for image proxy cache (configurable in Admin Settings)
  - Plus storage for uploaded location data, logs, and application files
- ARM or x64 CPU (Raspberry Pi 3+ or equivalent)

Image optimization is intentionally limited to still/single-frame JPEG, PNG, WebP, and GIF inputs. Multi-frame inputs are rejected as decoded-resource policy violations before full decode, while `Optimize=false` remains byte-for-byte pass-through without identification or decode. Accepted optimized images are independently bounded to 8,192 pixels per dimension, 12,000,000 pixels, exactly one frame, and a conservative width × height × 4 estimate capped at 64 MiB. The dedicated ImageSharp allocator's 128 MiB allocation-group limit and 128 MiB retained pool provide defense in depth; they are not total request or process-memory quotas.

The supporting Linux x64 memory observation was bounded and did not include APNG. Precise native allocation was unavailable, and its workload peak and complete-command peak covered different measurement scopes. It therefore supports only the tentative 1 GiB rationale above when the encoded ceiling is at most 50 MiB, not a universal memory guarantee.

**Recommended:**
- 2+ GB RAM
- **10+ GB disk space** (allows for user data growth and cache expansion)
- Multi-core CPU

### Software Requirements

- **Operating System:** Linux (Ubuntu 20.04+, Debian 11+, or similar)
- **Domain Name:** (Optional but recommended for HTTPS)
- **Root or sudo access**

---

## System User Setup

Create a dedicated system user to run Wayfarer:

```bash
# Create a system user without login shell
sudo useradd -r -s /bin/false wayfarer

# Or create a regular user if you want SSH access
sudo useradd -m -s /bin/bash wayfarer
sudo passwd wayfarer  # Set password if needed
```

---

## Install Dependencies

### 1. Update System

```bash
sudo apt update && sudo apt upgrade -y
```

### 2. Install .NET 10 SDK

```bash
# Download Microsoft package repository
wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

# Install .NET 10 SDK
sudo apt update
sudo apt install -y dotnet-sdk-10.0

# Verify installation
dotnet --version
# Should output: 10.0.x
```

**Note:** For Ubuntu/Debian versions or ARM devices, see: <https://learn.microsoft.com/en-us/dotnet/core/install/linux>

### 3. Install PostgreSQL with PostGIS

```bash
# Install PostgreSQL and PostGIS extension
sudo apt install -y postgresql postgresql-contrib postgis postgresql-16-postgis-3

# Start PostgreSQL service
sudo systemctl start postgresql
sudo systemctl enable postgresql

# Verify installation
psql --version
```

### 4. Install Nginx

```bash
sudo apt install -y nginx
sudo systemctl start nginx
sudo systemctl enable nginx
```

### 5. Install Node.js/npm Build Tooling

Server-build deployments need Node.js/npm on the build host so `deploy.sh` can
build the Trip Editor Vue/Vite assets before `dotnet publish`. Node/npm are
build-host tooling only; production does not run a Node service or SSR server.

`deployment/install.sh` installs or verifies Node.js 24.x/npm automatically. For
manual setup:

```bash
curl -fsSL https://deb.nodesource.com/setup_24.x | sudo -E bash -
sudo apt install -y nodejs
node --version
npm --version
```

### 6. Install Chromium Runtime Dependencies (PDF export)

Wayfarer uses **Microsoft Playwright** to render PDFs. Install system libraries:

```bash
sudo apt update && sudo apt install -y \
  xdg-utils libnss3 libnspr4 libatk1.0-0t64 libatk-bridge2.0-0t64 \
  libcups2t64 libdrm2 libdbus-1-3 libxkbcommon0 libxcomposite1 \
  libxdamage1 libxfixes3 libxrandr2 libgbm1 libasound2t64 \
  libpango-1.0-0 libcairo2 libxshmfence1 \
  fonts-liberation fonts-noto fonts-noto-cjk fonts-noto-color-emoji
```

> Note: `t64` package names are for Ubuntu/Debian 24.04+. On older versions, use names without `t64` suffix.

Browser binaries are immutable deployment dependencies. Wayfarer never downloads,
installs, or updates Chromium during requests, exports, or startup. Provision the
bundle matching the release's Microsoft.Playwright package before enabling browser
features; do not substitute an unrelated system Chromium. Startup itself does not
launch Chromium. Missing executables or OS libraries fail the browser operation
with provisioning guidance and the original launch error.

After publishing, provision explicitly (PowerShell is required for the generated
installer). On Ubuntu 24.04, `libasound2t64` supplies `libasound.so.2`; an executable
alone is insufficient. The generated installer's `install-deps chromium` command
is the version-coupled authority for the complete OS package set:

```bash
# Run as the deployment administrator, outside request processing.
sudo pwsh /path/to/publish/playwright.ps1 install-deps chromium
sudo env PLAYWRIGHT_BROWSERS_PATH=/opt/wayfarer-browsers \
  pwsh /path/to/publish/playwright.ps1 install chromium
# Give the service read/execute access, not ownership of browser binaries.
```

Set `Environment=PLAYWRIGHT_BROWSERS_PATH=/opt/wayfarer-browsers` in the native
systemd service override and restart after provisioning. Retain version-matched
bundles for releases still eligible for rollback. Development/test installation
is also explicit; see [Testing](22-Testing.md#net-playwright-rendering-test).
The #603 image child owns packaging these same dependencies into the final image.
There is no application executable override or host-location scanning.

Playwright owns process-scoped profiles and downloads in OS temporary storage and
cleans them when the browser closes. Keep OS temp outside the publish tree (for
example, set `TMPDIR=/tmp/wayfarer` to an existing writable directory). These files
are ephemeral, not cache or recovery data. Wayfarer-owned temporary files continue
to use `Storage:TempRoot`.

---

## Clone the Application

```bash
# Create directory structure
sudo mkdir -p /var/www/wayfarer
sudo chown wayfarer:wayfarer /var/www/wayfarer

# Switch to wayfarer user
sudo -u wayfarer bash
cd /var/www/wayfarer

# Clone the repository
git clone https://github.com/yourusername/wayfarer.git .
exit
```

---

## Database Setup

```bash
sudo -u postgres psql
```

```sql
-- Create database
CREATE DATABASE wayfarer;

-- Create user with password
CREATE USER wayfareruser WITH PASSWORD 'your-secure-password-here';

-- Grant privileges
GRANT ALL PRIVILEGES ON DATABASE wayfarer TO wayfareruser;

-- Connect to the wayfarer database
\c wayfarer

-- Enable PostGIS extension
CREATE EXTENSION IF NOT EXISTS postgis;

-- Grant schema privileges (required for PostgreSQL 15+)
GRANT ALL ON SCHEMA public TO wayfareruser;

\q
```

---

## Application Configuration

### Configure Connection String

The database password should be configured via systemd environment variable (not stored in appsettings.json files).

#### Automated Setup (install.sh)

The `install.sh` script automatically:
1. Prompts for the database password during installation
2. Creates the PostgreSQL user with that password
3. Configures the systemd service with the connection string
4. Installs or verifies Node.js 24.x/npm build tooling

For non-interactive installation, set the `DB_PASS` environment variable:

```bash
DB_PASS="your-secure-password" ./deployment/install.sh --non-interactive
```

#### Manual Configuration

If configuring manually, edit the systemd service file:

```bash
sudo nano /etc/systemd/system/wayfarer.service
```

Add under `[Service]`:

```ini
Environment="ConnectionStrings__DefaultConnection=Host=localhost;Database=wayfarer;Username=wayfareruser;Password=your-secure-password-here"
```

Then reload systemd:

```bash
sudo systemctl daemon-reload
sudo systemctl restart wayfarer
```

**Note:** The `appsettings.json` file contains a placeholder password (`CHANGE_ME_BEFORE_DEPLOY`). The systemd environment variable overrides this at runtime, which is more secure than storing passwords in config files.

---

## Directory Structure & Permissions

New location imports use `/var/lib/wayfarer/uploads/imports`, prepared with application-user ownership by both native scripts. The existing Linux-oriented `appsettings.Production.json` supplies `Storage:DataRoot=/var/lib/wayfarer`, `CacheRoot=/var/cache/wayfarer`, `LogRoot=/var/log/wayfarer`, and `TempRoot=/tmp/wayfarer`. Override these through normal ASP.NET Core configuration; when overriding DataRoot, prepare its `uploads/imports` directory for the service user before startup. Imports, tiles, images, generated thumbnails and file logs use these roots; Data Protection uses Storage defaults subject to explicit and previous-default compatibility; follow [F2 activation](24-Personal-Location-Providers.md#f1-preparation-to-f2-activation).

Native install/deploy also prepare `/var/cache/wayfarer/thumbnails/trips` and
`/var/log/wayfarer` with application-user ownership before startup. The Nginx
`location ^~ /thumbs/` proxy must reach Kestrel ahead of the generic image regex;
Kestrel serves external JPEGs and owns their versioned cache headers and 404s.
Native deployment no longer preserves `ChromeCache`, application-root `Logs`, or
`wwwroot/thumbs`: they are inactive residue, not runtime authorities. Deployments
may replace that obsolete payload. No migration/copy of old bytes is performed.
The `Uploads`, `TileCache`, and `ImageCache` exclusions remain solely for bounded
same-host legacy references. Preserve these trees, their deployment exclusions and
required write permissions until explicit migration (#604). Recognized old state
can still mutate them: TileCache revalidation/cold refill replaces legacy bytes,
legacy import deletion removes files beneath old `Uploads/Temp`, and ImageCache
refresh/LRU can retire legacy bytes after metadata authority changes.

For #609 closure, the current application/publish payload is replaceable and
read-only-qualified, and all current/new runtime authorities are externalized.
The three retained native legacy trees are bounded writable compatibility
authorities, not part of that read-only claim. An existing native installation
with retained legacy references cannot make these trees read-only today.
Fresh/current deployments do not depend on them. Removing source-checkout
scaffolding does not migrate or delete deployed legacy state; #604 owns that
explicit migration.

New import rows store logical `imports/<guidN><extension>` references. Old same-host absolute rows/files remain in place under known `Uploads/Temp` roots, with the deployment exclusion retained. Cross-host/native-to-Docker conversion needs a future explicit, quiesced migration; startup and Admin viewing never perform it. No EF schema migration is introduced for this reference change. Routine backup classification is owned by #533 and the real M6 migration by #604.

Native deployment scripts still prepare these legacy compatibility directories
if missing; current/new runtime writes do not depend on them:

- `TileCache/` - Retained legacy map tiles; new tiles use `Storage:CacheRoot/tiles`
- `ImageCache/` - Retained legacy proxied images; new writes use `Storage:CacheRoot/images` (LRU-evicted, admin-configurable size)
- `Uploads/` - Retained legacy upload compatibility tree; do not delete it while old rows reference it.

```bash
# Ensure wayfarer user owns the entire application
sudo chown -R wayfarer:wayfarer /var/www/wayfarer

# Set directory permissions
sudo find /var/www/wayfarer -type d -exec chmod 755 {} \;
sudo find /var/www/wayfarer -type f -exec chmod 644 {} \;
```

---

## Build & Test Locally

```bash
sudo -u wayfarer bash
cd /var/www/wayfarer

# Restore and build frontend prerequisites
dotnet restore
dotnet tool restore
dotnet frontend build
npm ci
npm run build

# Publish and run the production-like output
dotnet publish Wayfarer.csproj -c Release -o ./out
cd ./out
export ASPNETCORE_ENVIRONMENT=Production
dotnet Wayfarer.dll --urls=http://localhost:5000
```

Run production-like bundle acceptance from the published output. Do not use
source-tree `ASPNETCORE_ENVIRONMENT=Production dotnet run` for this check:
local scoped CSS such as `Wayfarer.styles.css` is a static web asset generated
outside `wwwroot`, while publish produces the supported production asset layout.
The Trip Editor published output must include
`wwwroot/vite/trip-editor/manifest.json` plus the CSS/JS files referenced by
that manifest.

**Before the first Production start:** provision `postgis` and `citext`, then run
`dotnet Wayfarer.dll database migrate`, `dotnet Wayfarer.dll database seed`, and
`dotnet Wayfarer.dll admin bootstrap <username> --stdin < /protected/password-file`
from the published directory under the service identity/configuration. For an existing
native administrator, use `admin reset <username> --stdin` instead of bootstrap.
The known Development default is never accepted as secure Production bootstrap.
See [maintenance boundaries](26-Application-Container.md#explicit-maintenance).

---

## Nginx Reverse Proxy Setup

```bash
sudo nano /etc/nginx/sites-available/wayfarer
```

```nginx
server {
    listen 80;
    server_name yourdomain.com;

    client_max_body_size 250M;
    access_log /var/log/nginx/wayfarer.access.log combined;

    location / {
        proxy_pass         http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header   Host $host;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;

        # SSE configuration
        proxy_buffering     off;
        proxy_cache         off;
        proxy_read_timeout  3600s;
    }

    add_header X-Content-Type-Options "nosniff" always;
    add_header X-Frame-Options "SAMEORIGIN" always;
}
```

```bash
sudo ln -s /etc/nginx/sites-available/wayfarer /etc/nginx/sites-enabled/
sudo nginx -t
sudo systemctl reload nginx
```

---

## HTTPS with Let's Encrypt

```bash
sudo apt install -y certbot python3-certbot-nginx
sudo certbot --nginx -d yourdomain.com
sudo certbot renew --dry-run
```

---

## Running as a System Service

```bash
sudo nano /etc/systemd/system/wayfarer.service
```

```ini
[Unit]
Description=Wayfarer Location Tracking Application
After=network.target

[Service]
User=wayfarer
WorkingDirectory=/var/www/wayfarer
ExecStart=/usr/bin/dotnet /var/www/wayfarer/Wayfarer.dll --urls http://localhost:5000
Restart=always
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=HOME=/home/wayfarer
# Contact email for tile provider User-Agent (OSM compliance)
Environment=Application__ContactEmail=admin@your-domain.example
# One exact public hostname authorized for the origin-only tile provider Referer
Environment="AllowedHosts=wayfarer.example.com"
# Multiple exact public hostnames
Environment="AllowedHosts=wayfarer.example.com;www.wayfarer.example.com"

[Install]
WantedBy=multi-user.target
```

Replace the examples with semicolon-separated exact public DNS hostnames. Do not include wildcards, IP literals, localhost/private names, ports, or URL schemes. A wildcard cannot establish a trustworthy deployment origin, so Wayfarer omits the upstream Referer and providers may respond with 403.

```bash
sudo systemctl daemon-reload
sudo systemctl enable wayfarer
sudo systemctl start wayfarer
```

---

## Post-Installation

1. Access at `https://yourdomain.com`
2. Log in with the administrator and protected password supplied during explicit bootstrap.
3. Keep the initial password outside shell history and ordinary configuration.
4. Configure settings in Admin Dashboard

---

## Updating Wayfarer

Before F2 startup, follow the source preparation and activation sequence in [Personal Location Providers](24-Personal-Location-Providers.md#f1-preparation-to-f2-activation). Preserve the resolved active key ring; existing explicit `/home/wayfarer/.aspnet/DataProtection-Keys` overrides remain installed until migration. Back up and restore the PostgreSQL database and key ring together; restoration, ownership, and permission recovery must be complete before starting Wayfarer. Database-only backups are incomplete once protected credentials exist.

### Hidden Area SRID correction after v1.9.18

Deploy migration `20260922182107_RepairHiddenAreaSrid` together with the corrected application. Stop the old application before applying pending EF migrations through the normal deployment workflow, and start the corrected application only after migrations succeed. An application-only update does not repair saved polygons; leaving old application instances running can recreate SRID-0 drawings after repair.

For this upgrade, explicitly stop the service before invoking `deployment/deploy.sh`: its ordinary stop step occurs after migration execution. For manual deployment, apply pending migrations with `dotnet ef database update --project Wayfarer.csproj --context Wayfarer.Models.ApplicationDbContext` using the configured deployment environment/connection while the service is stopped, before the start step below. If migration fails, keep the service stopped until the data issue is resolved.

The migration locks Hidden Areas against concurrent writes, checks for unexpected nonzero SRIDs, and assigns SRID 4326 only to SRID-0 polygons with `ST_SetSRID`, within the normal EF migration transaction. Drawing coordinates already use longitude/latitude: no coordinates are transformed, existing 4326 records are unchanged, and an empty table is a no-op. Public points and statistics continue using the shared privacy query, including Hidden Area interior exclusion; private owner statistics remain complete.

If the preflight aborts, inspect `ST_SRID("Area")` in `"HiddenAreas"` and verify the unexpected records' source coordinate system before correcting them and retrying. The failure leaves all polygons unrepaired and the migration unapplied. Do not blindly relabel another coordinate system, delete Hidden Areas, or disable privacy.

`Down` deliberately retains corrected SRID metadata: it cannot distinguish repaired records from originally correct or subsequently created 4326 polygons. A downgrade is not a reversal of this data repair; indiscriminately resetting polygons to 0 would recreate the public-query failure. Keep the corrected writer during application rollback planning, or restore a verified matching database/application backup with the service stopped.

### Automated (Recommended)

```bash
cd /home/youruser/Wayfarer
./deployment/deploy.sh
```

### Manual

```bash
# Backup
sudo -u postgres pg_dump wayfarer > wayfarer-backup-$(date +%Y%m%d).sql

# Pull and build
cd /home/youruser/Wayfarer
git pull origin main
dotnet tool restore
dotnet frontend build
npm ci
npm run build
dotnet publish -c Release -o ./out

# Deploy
sudo systemctl stop wayfarer
sudo rsync -av --delete \
  --exclude 'Uploads' --exclude 'TileCache' --exclude 'ImageCache' \
  ./out/ /var/www/wayfarer/
sudo chown -R wayfarer:wayfarer /var/www/wayfarer
sudo systemctl start wayfarer
```

---

## Logging & Auditing

### Serilog Configuration

- Configured in `Program.cs` using console and rolling file sinks.
- PostgreSQL sink writes to table `AuditLogs` (auto-created if absent).

### Configuration

- `Storage:LogRoot` — ensure directory exists and is writable.
- `Logging:LogLevel:*` — tune verbosity. Development uses more verbose levels.

### Middleware

- `PerformanceMonitoringMiddleware` logs request timings.
- `DynamicRequestSizeMiddleware` sets max request body size from runtime settings.

### Audit

- User and admin actions are logged in database and file.
- Do not log secrets or passwords.

### Retention

- `LogCleanupJob` prunes log files older than 1 month automatically.
- `AuditLogCleanupJob` removes audit entries older than 2 years.

### View Logs

```bash
# Systemd journal
sudo journalctl -u wayfarer -f

# Application log files
sudo tail -f /var/log/wayfarer/wayfarer-*.log
```

---

## CLI Commands

List app CLI commands:

```bash
dotnet run --no-launch-profile -- help
```

### Password Reset

Reset a user's password from the command line:

```bash
dotnet Wayfarer.dll admin reset <username> --stdin < /protected/password-file
```

Run from the published directory with the service configuration. This uses Identity without starting web/jobs. The old password-argv form remains deprecated compatibility only; use protected stdin for new automation.

### Admin Maintenance

Additional admin tasks are available via Admin UI (Users, Roles, Jobs, Settings) rather than CLI.

---

## Troubleshooting

### Application Won't Start

```bash
sudo systemctl status wayfarer
sudo journalctl -u wayfarer -n 50
```

**Common issues:**
- Database connection failed → Check connection string
- Permission denied → Check directory permissions
- Port already in use → Check if port 5000 is available

### Database Connection Errors

```bash
psql -h localhost -U wayfareruser -d wayfarer
sudo systemctl status postgresql
```

### Nginx 502 Bad Gateway

```bash
sudo systemctl status wayfarer
sudo ss -tlnp | grep 5000
sudo tail -f /var/log/nginx/error.log
```

### PDF Export / Playwright Issues

```bash
# Check the explicitly provisioned bundle and OS dependency package.
ls -la /opt/wayfarer-browsers/
dpkg -l | grep -E 'libnss3|libgbm1|libasound2|libxshmfence'
# Diagnose missing libraries on the actual executable reported by Playwright.
ldd /opt/wayfarer-browsers/chromium_headless_shell-*/chrome-headless-shell-linux64/chrome-headless-shell
# Provision using this release's generated installer as documented above.
# Do not delete a shared bundle to repair one application operation.
```

### Out of Disk Space

```bash
df -h
sudo du -sh /var/www/wayfarer/*
```

**Clean up:**
- Logs: Auto-cleaned by LogCleanupJob
- Tile cache: Admin → Settings → Clear Tile Cache
- Image cache: Auto-evicted via LRU; size configurable in Admin → Settings
- Uploads: User → Import History

---

## Security Hardening / Rate Limiting / Fail2ban

### Nginx Rate Limiting

Template: `deployment/nginx-ratelimit.conf`

Includes rate-limit zones, per-IP connection limits, security headers, and scanner blocking.

### Fail2ban

```bash
# Install filters and jails
sudo cp deployment/fail2ban/wayfarer-nginx-*.conf /etc/fail2ban/filter.d/
sudo cp deployment/fail2ban/wayfarer-nginx.conf /etc/fail2ban/jail.d/
sudo systemctl restart fail2ban

# Verify
sudo fail2ban-client status wayfarer-nginx-scanner
sudo fail2ban-client status wayfarer-nginx-404
sudo fail2ban-client status wayfarer-nginx-login
```

---

## Security Checklist

### Resumable enrichment deployment and rollback

Run all required database migrations before starting any affected scheduler or service. Quartz schema validation must complete before scheduler startup. The supported topology has one active Quartz scheduler; do not run multiple active schedulers until clustering is explicitly configured and validated. After restart, active workflows should have one stable job/current trigger and paused or terminal workflows no live trigger.

Before rollback, stop the scheduler and back up PostgreSQL plus the Data Protection key ring. Removing the additive workflow/attempt migrations removes scheduling metadata only. Never delete imported Locations, successful enrichment/provenance, protected credentials, Geoapify admissions, Mapbox meters, or import data as rollback cleanup.

- [ ] Changed default admin password
- [ ] Database user has strong password
- [ ] HTTPS is enabled (Let's Encrypt)
- [ ] Firewall configured (only ports 80, 443, SSH open)
- [ ] Application running as non-root user
- [ ] Regular database backups scheduled
- [ ] `appsettings.json` has correct file permissions
- [ ] Monitoring configured for disk space
- [ ] Nginx rate limiting configured
- [ ] Fail2ban installed and configured

---

## Support

- **GitHub Issues:** Report bugs and feature requests
- **Documentation:** See `docs/` folder
- **Configuration Reference:** `docs/16-Configuration.md`


Native TileCache transition (#617): install/deploy prepares `/var/cache/wayfarer/tiles` with application-user ownership and retains the old deployed `TileCache` exclusion/tree. Operators overriding `Storage__CacheRoot` must prepare the corresponding `tiles` directory before startup. Preserve customized `CacheSettings:TileCacheDirectory` as the temporary legacy-root input. This does not copy old files or rewrite DB paths; see [cache configuration](16-Configuration.md).

Native ImageCache transition (#623): new writes use `StoragePaths.Images`, and install/deploy prepares `/var/cache/wayfarer/images` with application-user ownership. Retain `$DEPLOY_DIR/ImageCache`, its rsync exclusion and existing write permissions for bounded legacy reads and refresh/LRU retirement. Keep customized `CacheSettings:ImageCacheDirectory` as the deprecated legacy-root input. Operators overriding `Storage__CacheRoot` must prepare its `images` subdirectory before startup. New metadata uses logical `.dat` filenames; legacy reads remain in place, while successful refresh promotes to a current-root generation only after metadata commit. No bulk migration or EF schema migration is introduced; routine ImageCache backups remain excluded/rebuildable. See [configuration](16-Configuration.md) for explicit compatible-cache migration guidance.
