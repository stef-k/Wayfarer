# Deployment & Operations

This guide covers installation, deployment, logging, and operational commands for Wayfarer on Linux servers.

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
- 1 GB RAM
- **5 GB disk space** minimum:
  - ~2 GB for tile cache (zoom <= 8: ~1 GB, zoom >= 9: 1 GB configurable)
  - ~512 MB for image proxy cache (configurable in Admin Settings)
  - Plus storage for uploaded location data, logs, and application files
- ARM or x64 CPU (Raspberry Pi 3+ or equivalent)

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

### 5. Install Chromium Runtime Dependencies (PDF export)

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

Playwright will **automatically download** Chromium (~400MB) to `ChromeCache/playwright-browsers/` on first PDF export.

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

The following directories are **auto-created** if missing:

- `Logs/` - Application log files (auto-cleaned after 1 month)
- `TileCache/` - Cached map tiles
- `ImageCache/` - Cached proxied images (LRU-evicted, admin-configurable size)
- `ChromeCache/` - Chrome browser binaries for PDF export
- `Uploads/` - User uploaded files

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

# Restore and build
dotnet restore
dotnet build -c Release

# Test run
export ASPNETCORE_ENVIRONMENT=Production
dotnet run --urls=http://localhost:5000
```

**What happens on first run:**
- Database tables created automatically
- Default admin user: `admin` / `Admin1!`
- System roles seeded
- 69 activity types seeded

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

[Install]
WantedBy=multi-user.target
```

```bash
sudo systemctl daemon-reload
sudo systemctl enable wayfarer
sudo systemctl start wayfarer
```

---

## Post-Installation

1. Access at `https://yourdomain.com`
2. Login: `admin` / `Admin1!`
3. **Change admin password immediately!**
4. Configure settings in Admin Dashboard

---

## Updating Wayfarer

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
dotnet publish -c Release -o ./out

# Deploy
sudo systemctl stop wayfarer
sudo rsync -av --delete \
  --exclude 'Uploads' --exclude 'TileCache' --exclude 'ImageCache' --exclude 'ChromeCache' --exclude 'Logs' \
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

- `Logging:LogFilePath:Default` — ensure directory exists and is writable.
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

### Password Reset

Reset a user's password from the command line:

```bash
dotnet run -- reset-password <username> <new-password>
```

This spins up minimal services, generates a reset token, and updates the password. Use temporary values and rotate immediately.

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
# Check Chromium download
ls -la /var/www/wayfarer/ChromeCache/playwright-browsers/

# Check dependencies
dpkg -l | grep -E 'libnss3|libgbm1|libasound2|libxshmfence'

# Force reinstall
sudo rm -rf /var/www/wayfarer/ChromeCache/playwright-browsers
sudo -u wayfarer bash
cd /var/www/wayfarer
pwsh bin/Release/net9.0/playwright.ps1 install chromium
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

