# Wayfarer Installation & Deployment Guide

This guide covers complete installation and deployment of Wayfarer on Linux servers (Raspberry Pi, VPS, dedicated servers). The application targets power users, small businesses, and organizations who want to self-host their location tracking and trip planning solution.

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
14. [Troubleshooting](#troubleshooting)
15. [Security Hardening / Rate Limiting / Fail2ban](#security-hardening--rate-limiting--fail2ban)
16. [Security Checklist](#security-checklist)

---

## Prerequisites

### Hardware Requirements

**Minimum:**

- 1 GB RAM
- **5 GB disk space** minimum:
  - ~2 GB for tile cache (zoom <= 8: ~1 GB, zoom >= 9: 1 GB configurable)
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

Create a dedicated system user to run Wayfarer (recommended for security):

```bash
# Create a system user without login shell
sudo useradd -r -s /bin/false wayfarer

# Or create a regular user if you want SSH access
sudo useradd -m -s /bin/bash wayfarer
sudo passwd wayfarer  # Set password if needed
```

For the rest of this guide, we'll use the system user `wayfarer`.

---

## Install Dependencies

### 1. Update System

```bash
sudo apt update && sudo apt upgrade -y
```

### 2. Install .NET 9 SDK

```bash
# Download Microsoft package repository
wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb
rm packages-microsoft-prod.deb

# Install .NET 9 SDK
sudo apt update
sudo apt install -y dotnet-sdk-9.0

# Verify installation
dotnet --version
# Should output: 9.0.x
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
# Should output: psql (PostgreSQL) 16.x
```

### 4. Install Nginx

```bash
sudo apt install -y nginx

# Start and enable nginx
sudo systemctl start nginx
sudo systemctl enable nginx

# Verify
sudo systemctl status nginx
```

### 5. Install Git (if not already installed)

```bash
sudo apt install -y git
```

# 6. Install Chromium Runtime Dependencies (PDF export)

Wayfarer uses **Microsoft Playwright** to render PDFs.
Playwright automatically downloads Chromium binaries for **all platforms** including ARM64 Linux.

## All Platforms (x64 & ARM64 Linux)

Install system libraries required by Chromium:

```bash
sudo apt update && sudo apt install -y \
  xdg-utils libnss3 libnspr4 libatk1.0-0t64 libatk-bridge2.0-0t64 \
  libcups2t64 libdrm2 libdbus-1-3 libxkbcommon0 libxcomposite1 \
  libxdamage1 libxfixes3 libxrandr2 libgbm1 libasound2t64 \
  libpango-1.0-0 libcairo2 libxshmfence1 \
  fonts-liberation fonts-noto fonts-noto-cjk fonts-noto-color-emoji
```

> Note: `t64` package names are correct on Ubuntu/Debian 24.04+. On older versions, use package names without `t64` suffix (e.g., `libasound2` instead of `libasound2t64`).

## Chromium Binary Installation

Playwright will **automatically download** Chromium (~400MB) to `ChromeCache/playwright-browsers/` on first PDF export.

**To pre-install Chromium during deployment** (recommended to avoid slow first export):

```bash
# After building the application
cd /var/www/wayfarer
pwsh bin/Release/net9.0/playwright.ps1 install chromium

# Or let it auto-download on first PDF export
```

This works on **all platforms**:

- ✅ Linux x64
- ✅ Linux ARM64 (Raspberry Pi, ARM servers)
- ✅ Windows x64/ARM64
- ✅ macOS x64/ARM64

**No system Chromium needed!** Playwright handles everything automatically.

---

# 7. Ensure service user has a HOME directory

Playwright-managed Chromium needs a writable HOME directory for runtime profile data:

```bash
# Ensure wayfarer user has a home directory
sudo usermod -d /home/wayfarer wayfarer
sudo mkdir -p /home/wayfarer
sudo chown -R wayfarer:wayfarer /home/wayfarer
```

**Note:** The `ChromeCache/` directory will be created automatically by the application. Playwright will use `ChromeCache/playwright-browsers/` to store its Chromium binary.

---

# 8. Systemd service environment

Set `HOME` so Playwright's Chromium can create runtime profiles:

```ini
# /etc/systemd/system/wayfarer.service
[Unit]
Description=Wayfarer ASP.NET Core App
After=network.target

[Service]
User=wayfarer
WorkingDirectory=/var/www/wayfarer
ExecStart=/usr/bin/dotnet Wayfarer.dll --urls http://localhost:5000
Restart=on-failure
Environment=DOTNET_ENVIRONMENT=Production
Environment=HOME=/home/wayfarer

[Install]
WantedBy=multi-user.target
```

Apply:

```bash
sudo systemctl daemon-reload
sudo systemctl restart wayfarer
```

**Note:** Playwright will automatically download Chromium to `ChromeCache/playwright-browsers/` on first PDF export. See [PDF Export Troubleshooting](#pdf-export--playwright-issues) if you encounter errors.

---

## Clone the Application

### 1. Create Application Directory

```bash
# Create directory structure
sudo mkdir -p /var/www/wayfarer
sudo chown wayfarer:wayfarer /var/www/wayfarer
```

### 2. Clone Repository

```bash
# Switch to wayfarer user
sudo -u wayfarer bash

# Navigate to app directory
cd /var/www/wayfarer

# Clone the repository
git clone https://github.com/yourusername/wayfarer.git .

# Or download and extract release
# wget https://github.com/yourusername/wayfarer/archive/refs/tags/v1.0.0.tar.gz
# tar -xzf v1.0.0.tar.gz --strip-components=1

# Exit wayfarer user shell
exit
```

---

## Database Setup

### 1. Create Database and User

```bash
# Switch to postgres user
sudo -u postgres psql

# Run the following SQL commands:
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

-- Exit psql
\q
```

### 2. Test Database Connection

```bash
# Test connection
psql -h localhost -U wayfareruser -d wayfarer -c "SELECT version();"

# Enter password when prompted
# Should display PostgreSQL version
```

**Security Note:** For local connections, you may want to configure PostgreSQL to use peer authentication. Edit `/etc/postgresql/16/main/pg_hba.conf`:

```
# IPv4 local connections:
local   wayfarer        wayfareruser                            md5
host    wayfarer        wayfareruser    127.0.0.1/32            md5
host    wayfarer        wayfareruser    ::1/128                 md5
```

Then reload PostgreSQL:

```bash
sudo systemctl reload postgresql
```

---

## Application Configuration

### 1. Edit Connection String

```bash
# Switch to wayfarer user
sudo -u wayfarer bash
cd /var/www/wayfarer

# Edit appsettings.json
nano appsettings.json
```

Update the following sections:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=wayfarer;Username=wayfareruser;Password=your-secure-password-here"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Wayfarer.Middleware.PerformanceMonitoringMiddleware": "Information"
    },
    "LogFilePath": {
      "Default": "/var/log/wayfarer/wayfarer-.log"
    }
  },
  "CacheSettings": {
    "TileCacheDirectory": "TileCache",
    "ChromeCacheDirectory": "ChromeCache"
  },
  "AllowedHosts": "*"
}
```

**Connection String Options:**

- **TCP Connection:** `Host=localhost;Port=5432;Database=wayfarer;Username=wayfareruser;Password=yourpassword`
- **Unix Socket:** `Host=/var/run/postgresql;Database=wayfarer;Username=wayfareruser;Password=yourpassword` (faster, local only)

**Cache Directory Notes:**

- **TileCache/ChromeCache:** Default relative paths work for both development and production
- Relative paths resolve to the application's working directory
- In production (systemd service), this becomes `/var/www/wayfarer/TileCache` and `/var/www/wayfarer/ChromeCache`
- For custom paths, use absolute paths in `appsettings.Production.json`

### 2. Configure for Production (Optional)

Create `appsettings.Production.json` for production-specific settings:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning",
      "Wayfarer.Middleware.PerformanceMonitoringMiddleware": "Information"
    },
    "LogFilePath": {
      "Default": "/var/log/wayfarer/wayfarer-.log"
    }
  },
  "CacheSettings": {
    "TileCacheDirectory": "/var/www/wayfarer/TileCache",
    "ChromeCacheDirectory": "/var/www/wayfarer/ChromeCache"
  }
}
```

**Note:** The repository now includes a production configuration template. You can customize it for your deployment.

Exit the wayfarer user shell:

```bash
exit
```

---

## Directory Structure & Permissions

### 1. Understanding the Directory Structure

The following directories are **included in the repository** and will be cloned automatically:

- `Logs/` - Application log files (auto-created if missing, auto-cleaned after 1 month)
- `TileCache/` - Cached map tiles (auto-created if missing)
- `ChromeCache/` - Chrome browser binaries for PDF export (auto-downloaded on first PDF export)
- `Uploads/` - User uploaded files (auto-created if missing, includes `Temp/` subdirectory)

**The application automatically creates these directories if they don't exist**, so no manual creation is needed.

**Note:** ChromeCache stores the Playwright-managed Chromium browser used for PDF export. It's automatically downloaded (~400MB) to `ChromeCache/playwright-browsers/` when you first export a trip as PDF. This directory is preserved during updates.

### 2. Set Ownership and Permissions

After cloning, ensure the `wayfarer` user owns all application files and has write access to cache/log directories:

```bash
# Ensure wayfarer user owns the entire application
sudo chown -R wayfarer:wayfarer /var/www/wayfarer

# Set directory permissions (755 = rwxr-xr-x)
sudo find /var/www/wayfarer -type d -exec chmod 755 {} \;

# Set file permissions (644 = rw-r--r--)
sudo find /var/www/wayfarer -type f -exec chmod 644 {} \;
```

**Note:** The application requires write access to:

- `Logs/` - For writing application logs
- `TileCache/` - For caching map tiles
- `Uploads/` - For storing user uploaded location data

### 3. Directory Structure Overview

```
/var/www/wayfarer/
├── appsettings.json                 # Main configuration
├── appsettings.Production.json      # Production overrides
├── Program.cs                       # Application entry point
├── Wayfarer.csproj                  # Project file
├── Areas/                           # MVC Areas
├── Models/                          # Data models
├── Services/                        # Business logic
├── Jobs/                            # Background jobs (log cleanup, etc.)
├── wwwroot/                         # Static files (CSS, JS, images)
├── Logs/                            # Application logs (auto-cleaned monthly)
├── TileCache/                       # Map tile cache (~2 GB)
├── Uploads/                         # User location data uploads
│   └── Temp/                        # Temporary upload processing
└── ...
```

**Storage Notes:**

- `Logs/` - Automatically cleaned (files older than 1 month are deleted)
- Cache directories - Managed by admin settings (configurable size limits)
- `Uploads/` - User data, grows with usage

---

## Build & Test Locally

### 1. Build the Application

```bash
# Switch to wayfarer user
sudo -u wayfarer bash
cd /var/www/wayfarer

# Restore dependencies
dotnet restore

# Build the application
dotnet build -c Release

# Exit wayfarer user
exit
```

### 2. Test Run (First Time)

```bash
# Run as wayfarer user
sudo -u wayfarer bash
cd /var/www/wayfarer

# Set environment to production
export ASPNETCORE_ENVIRONMENT=Production

# Run the application (test mode)
dotnet run --urls=http://localhost:5000

# Watch the output - you should see:
# - Database migrations being applied
# - "Application started. Press Ctrl+C to shut down."
```

**What happens on first run:**

- Database tables are created automatically
- All migrations are applied
- Default admin user is created: `admin` / `Admin1!`
- System roles are seeded
- 69 activity types are seeded
- Application settings are initialized

**Test the application:**

Open another terminal and test:

```bash
# Test if app responds
curl http://localhost:5000

# Should return HTML of the home page
```

**Stop the test run:**
Press `Ctrl+C` in the terminal running the app.

```bash
# Exit wayfarer user
exit
```

---

## Nginx Reverse Proxy Setup

### 1. Create Nginx Configuration

**Basic Configuration (below)** or **Enhanced Configuration with Rate Limiting:**

For production deployments exposed to the internet, consider using the enhanced configuration with rate limiting. See [Security Hardening](#security-hardening--rate-limiting--fail2ban) section below for the complete template at `deployment/nginx-ratelimit.conf`.

**Basic configuration without rate limiting:**

```bash
# Create site configuration
sudo nano /etc/nginx/sites-available/wayfarer
```

```nginx
# Redirect www to non-www (optional)
server {
    listen 80;
    server_name www.yourdomain.com;
    return 301 https://yourdomain.com$request_uri;
}

# Main server configuration
server {
    listen 80;
    server_name yourdomain.com;

    # Increase max upload size (adjust as needed)
    client_max_body_size 250M;

    # Dedicated access log for Wayfarer (needed for Fail2ban)
    access_log /var/log/nginx/wayfarer.access.log combined;

    # Proxy WebSocket requests
    location /ws/ {
        proxy_pass         http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection "Upgrade";
        proxy_set_header   Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_set_header   X-Forwarded-Host $host;
    }

    # Proxy all other requests (SSE-compatible for real-time updates)
    location / {
        proxy_pass         http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header   Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_set_header   X-Forwarded-Host $host;

        # Server-Sent Events (SSE)
        proxy_buffering     off;
        proxy_cache         off;
        proxy_read_timeout  3600s;
        proxy_send_timeout  3600s;
        send_timeout        3600s;
    }

    # Security headers
    add_header X-Content-Type-Options "nosniff" always;
    add_header Referrer-Policy "no-referrer-when-downgrade" always;
    add_header X-Frame-Options "SAMEORIGIN" always;
}
```

### 2. Enable the Site

```bash
# Create symbolic link to enable site
sudo ln -s /etc/nginx/sites-available/wayfarer /etc/nginx/sites-enabled/

# Test nginx configuration
sudo nginx -t

# Should output:
# nginx: configuration file /etc/nginx/nginx.conf test is successful

# Reload nginx
sudo systemctl reload nginx
```

### 3. Configure Firewall (if using UFW)

```bash
# Allow HTTP and HTTPS
sudo ufw allow 'Nginx Full'

# Check status
sudo ufw status
```

---

## HTTPS with Let's Encrypt

### 1. Install Certbot

```bash
# Install certbot and nginx plugin
sudo apt install -y certbot python3-certbot-nginx
```

### 2. Obtain SSL Certificate

```bash
# Run certbot
sudo certbot --nginx -d yourdomain.com -d www.yourdomain.com

# Follow the prompts:
# - Enter email address for renewal notifications
# - Agree to terms of service
# - Choose whether to redirect HTTP to HTTPS (recommended: yes)
```

Certbot will automatically:

- Obtain SSL certificate from Let's Encrypt
- Modify your nginx configuration
- Set up automatic renewal

### 3. Verify Auto-Renewal

```bash
# Test renewal process
sudo certbot renew --dry-run

# Should output: "Congratulations, all simulated renewals succeeded"
```

### 4. Updated Nginx Configuration

After Certbot runs, your nginx config will look like this:

```nginx
# Redirect www to non-www
server {
    listen 80;
    server_name www.yourdomain.com;
    return 301 https://yourdomain.com$request_uri;
}

# Redirect HTTP to HTTPS
server {
    listen 80;
    server_name yourdomain.com;
    return 301 https://$host$request_uri;
}

# Main HTTPS server
server {
    listen 443 ssl http2;
    server_name yourdomain.com;

    # Dedicated access log for Wayfarer (needed for Fail2ban)
    access_log /var/log/nginx/wayfarer.access.log combined;

    # SSL configuration (managed by Certbot)
    ssl_certificate /etc/letsencrypt/live/yourdomain.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/yourdomain.com/privkey.pem;
    include /etc/letsencrypt/options-ssl-nginx.conf;
    ssl_dhparam /etc/letsencrypt/ssl-dhparams.pem;

    # Increase max upload size
    client_max_body_size 250M;

    # Proxy WebSocket requests
    location /ws/ {
        proxy_pass         http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header   Upgrade $http_upgrade;
        proxy_set_header   Connection "Upgrade";
        proxy_set_header   Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_set_header   X-Forwarded-Host $host;
    }

    # Proxy all other requests
    location / {
        proxy_pass         http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header   Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_set_header   X-Forwarded-Host $host;

        # SSE configuration
        proxy_buffering     off;
        proxy_cache         off;
        proxy_read_timeout  3600s;
        proxy_send_timeout  3600s;
        send_timeout        3600s;
    }

    # Security headers
    add_header X-Content-Type-Options "nosniff" always;
    add_header Referrer-Policy "no-referrer-when-downgrade" always;
    add_header X-Frame-Options "SAMEORIGIN" always;
}
```

---

## Running as a System Service

Create a systemd service to run Wayfarer automatically on boot.

### 1. Create Service File

Wayfarer includes a ready-to-use systemd service template.

**Option 1: Use the template (recommended)**

```bash
# From your repository clone directory
cd /home/youruser/Wayfarer

# Copy the template to systemd
sudo cp deployment/wayfarer.service /etc/systemd/system/wayfarer.service

# Edit if you need to customize paths or settings
sudo nano /etc/systemd/system/wayfarer.service
```

**Option 2: Create manually**

```bash
sudo nano /etc/systemd/system/wayfarer.service
```

Minimal configuration:

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

**Customization points:**

- `User`: Your application user (default: `wayfarer`)
- `WorkingDirectory`: Your deployment directory
- `--urls`: Listening address/port (default: `http://localhost:5000`)

See `deployment/wayfarer.service` for the full recommended configuration with all settings.

### 2. Publish the Application

Before running as a service, publish the app for production:

```bash
# Use the automated deployment script (recommended)
cd /home/youruser/Wayfarer
./deployment/deploy.sh

# OR manually:
cd /home/youruser/Wayfarer
dotnet publish -c Release -o ./out
sudo rsync -av --delete \
  --exclude 'Uploads' --exclude 'TileCache' --exclude 'ChromeCache' --exclude 'Logs' \
  ./out/ /var/www/wayfarer/
sudo chown -R wayfarer:wayfarer /var/www/wayfarer
```

### 3. Enable and Start Service

```bash
# Reload systemd daemon
sudo systemctl daemon-reload

# Enable service (start on boot)
sudo systemctl enable wayfarer

# Start the service
sudo systemctl start wayfarer

# Check status
sudo systemctl status wayfarer

# Should show: "Active: active (running)"
```

### 4. Service Management Commands

```bash
# Start service
sudo systemctl start wayfarer

# Stop service
sudo systemctl stop wayfarer

# Restart service
sudo systemctl restart wayfarer

# View logs
sudo journalctl -u wayfarer -f

# View logs (last 100 lines)
sudo journalctl -u wayfarer -n 100

# View logs since today
sudo journalctl -u wayfarer --since today
```

---

## Post-Installation

### 1. Access the Application

Open your browser and navigate to:

- HTTP: `http://yourdomain.com`
- HTTPS: `https://yourdomain.com`

### 2. Login as Admin

**Default admin credentials:**

- Username: `admin`
- Password: `Admin1!`

**⚠️ IMPORTANT: Change the admin password immediately!**

### 3. Change Admin Password

1. Login as admin
2. Navigate to your profile or admin settings
3. Change password to a strong, unique password

### 4. Configure Application Settings

Navigate to **Admin Dashboard** → **Settings**:

- **Registration:** Enable/disable user registration
- **Location Thresholds:** Adjust time/distance thresholds for location tracking
- **Upload Limits:** Set max file upload size
- **Tile Cache Size:** Configure map tile cache limits
- **Group Settings:** Enable/disable auto-deletion of empty groups

### 5. Create Regular Users

You can now:

- Enable user registration (Settings → Registration → Open)
- Or manually create users through the admin panel

### 6. Automatic Maintenance

Wayfarer includes **automated maintenance jobs** via Quartz Scheduler:

**Log Cleanup Job:**

- Runs automatically on a schedule
- Deletes log files older than **1 month**
- **No manual log rotation setup needed!**
- Configured in `Jobs/LogCleanupJob.cs`

**Audit Log Cleanup Job:**

- Cleans old audit log entries from the database
- Prevents database bloat over time

**Monitor jobs via Admin Dashboard:**

- Navigate to **Admin** → **Job History**
- View job execution status and logs
- Jobs are configured and scheduled automatically on first startup

---

## Updating Wayfarer

### Option 1: Automated Deployment Script (Recommended)

Wayfarer includes an automated deployment script that handles the entire update process.

**Setup (one-time):**

```bash
# Navigate to your repository clone
cd /home/youruser/Wayfarer

# Make the deploy script executable
chmod +x deployment/deploy.sh

# Edit the script to set your paths (if different from defaults)
nano deployment/deploy.sh
# Update APP_DIR, DEPLOY_DIR, APP_USER, SERVICE_NAME if needed
```

**Deploy latest from master:**

```bash
cd /home/youruser/Wayfarer
./deployment/deploy.sh
```

**Deploy specific branch or tag:**

```bash
# Deploy from develop branch
REF=develop ./deployment/deploy.sh

# Deploy from a release tag
REF=v1.2.0 ./deployment/deploy.sh
```

**What the script does:**

1. Pulls latest code from Git
2. Builds the application
3. Applies database migrations
4. Stops the service
5. Deploys files (preserving Uploads, TileCache, ChromeCache, Logs)
6. Fixes permissions
7. Ensures writable directories exist
8. Restarts the service

---

### Option 2: Manual Update Process

If you prefer manual control or need to customize the process:

#### 1. Backup Database

```bash
# Backup database
sudo -u postgres pg_dump wayfarer > wayfarer-backup-$(date +%Y%m%d).sql

# Or backup to compressed file
sudo -u postgres pg_dump wayfarer | gzip > wayfarer-backup-$(date +%Y%m%d).sql.gz
```

#### 2. Pull Latest Code

```bash
cd /home/youruser/Wayfarer  # Your repository clone location

# Stash any local changes (if any)
git stash

# Pull latest changes
git pull origin master

# Or checkout specific release
# git fetch --tags
# git checkout v1.1.0
```

#### 3. Build Application

```bash
cd /home/youruser/Wayfarer
dotnet publish -c Release -o ./out
```

#### 4. Apply Migrations

```bash
cd /home/youruser/Wayfarer
export PATH="$PATH:$HOME/.dotnet/tools"
DOTNET_ENVIRONMENT=Production dotnet ef database update --project Wayfarer.csproj
```

#### 5. Deploy and Restart

```bash
# Stop the service
sudo systemctl stop wayfarer

# Deploy files (excluding writable directories)
sudo rsync -av --delete \
  --exclude 'Uploads' \
  --exclude 'TileCache' \
  --exclude 'ChromeCache' \
  --exclude 'Logs' \
  /home/youruser/Wayfarer/out/ /var/www/wayfarer/

# Fix permissions
sudo chown -R wayfarer:wayfarer /var/www/wayfarer

# Ensure writable directories exist
sudo mkdir -p /var/www/wayfarer/{Uploads,TileCache,ChromeCache,Logs}
sudo chown -R wayfarer:wayfarer /var/www/wayfarer/{Uploads,TileCache,ChromeCache,Logs}

# Start the service
sudo systemctl start wayfarer

# Check status
sudo systemctl status wayfarer

# Watch logs for any issues
sudo journalctl -u wayfarer -f
```

---

## Troubleshooting

### Application Won't Start

**Check service status:**

```bash
sudo systemctl status wayfarer
sudo journalctl -u wayfarer -n 50
```

**Common issues:**

- Database connection failed → Check connection string in `appsettings.json`
- Permission denied → Check directory permissions
- Port already in use → Check if another process is using port 5000

### Database Connection Errors

**Test connection manually:**

```bash
psql -h localhost -U wayfareruser -d wayfarer
```

**Check PostgreSQL is running:**

```bash
sudo systemctl status postgresql
```

**Check `pg_hba.conf` authentication:**

```bash
sudo nano /etc/postgresql/16/main/pg_hba.conf
```

### Permission Errors

**Fix ownership:**

```bash
sudo chown -R wayfarer:wayfarer /var/www/wayfarer
sudo chown -R wayfarer:wayfarer /var/log/wayfarer
```

**Fix directory permissions:**

```bash
sudo chmod -R 755 /var/www/wayfarer/TileCache
sudo chmod -R 755 /var/www/wayfarer/Uploads
sudo chmod -R 755 /var/log/wayfarer
```

### Nginx 502 Bad Gateway

**Check if Wayfarer is running:**

```bash
sudo systemctl status wayfarer
```

**Check if app is listening on port 5000:**

```bash
sudo netstat -tlnp | grep 5000
# or
sudo ss -tlnp | grep 5000
```

**Check nginx error logs:**

```bash
sudo tail -f /var/log/nginx/error.log
```

### Application Logs

**View application logs:**

```bash
# Systemd journal
sudo journalctl -u wayfarer -f

# Application log files
sudo tail -f /var/log/wayfarer/wayfarer-*.log
```

### SSL Certificate Issues

**Renew certificate manually:**

```bash
sudo certbot renew
```

**Check certificate status:**

```bash
sudo certbot certificates
```

### Database Migration Issues

If migrations fail to apply automatically:

```bash
# Stop the service
sudo systemctl stop wayfarer

# Apply migrations manually
sudo -u wayfarer bash
cd /var/www/wayfarer
dotnet ef database update
exit

# Start the service
sudo systemctl start wayfarer
```

### Out of Disk Space

**Check disk usage:**

```bash
df -h
```

**Check which directories are consuming space:**

```bash
sudo du -sh /var/www/wayfarer/*
```

**Clean up:**

- **Logs:** Automatically cleaned after 1 month by LogCleanupJob (no manual action needed)
  - If needed urgently: `sudo find /var/www/wayfarer/Logs -name "*.log" -mtime +30 -delete`
- **Tile cache:** Navigate to **Admin → Settings → Clear Tile Cache** (or configure lower cache limits)
- **MBTiles cache:** Navigate to **Admin → Settings** and configure cache limits
- **Uploads:** Review and delete old location imports through the web interface (**User → Import History**)
- **Database:** Run audit log cleanup manually via **Admin → Job History** if needed

### PDF Export / Playwright Issues

**How Wayfarer handles Chromium across platforms:**

Wayfarer uses **Microsoft Playwright** to generate PDF exports. Chromium binaries are **NOT** included in the repository but are **automatically downloaded** by Playwright for all platforms.

**✅ All platforms automatically supported:**

- Windows x64/ARM64
- macOS x64/ARM64
- Linux x64
- **Linux ARM64** (Raspberry Pi, ARM servers) ✨ **NEW!**

→ Chromium is **automatically downloaded** to `ChromeCache/playwright-browsers/` on first PDF export

**Troubleshooting steps:**

**1. Check if Chromium dependencies are installed:**

```bash
# Verify libraries are present
dpkg -l | grep -E 'libnss3|libgbm1|libasound2|libxshmfence'
```

If missing, install them (see [Install Chromium Runtime Dependencies](#install-dependencies) section).

**2. Check Chromium download location:**

```bash
# Check if Playwright downloaded Chromium
ls -la /var/www/wayfarer/ChromeCache/playwright-browsers/
# Should show Chromium directory like: chromium-1130/

# Or check with find
find /var/www/wayfarer/ChromeCache -name "chrome" -type f
```

**3. Pre-install Chromium (recommended):**

If you want to avoid the ~400MB download on first PDF export:

```bash
# Switch to wayfarer user
sudo -u wayfarer bash
cd /var/www/wayfarer

# Install Playwright browsers
pwsh bin/Release/net9.0/playwright.ps1 install chromium

exit
```

**4. Check Chromium binary has execute permission:**

```bash
# Find and fix permissions
find /var/www/wayfarer/ChromeCache/playwright-browsers -name "chrome" -type f -exec chmod +x {} \;
```

**5. Test Chromium binary manually:**

```bash
# Find the chrome binary
CHROME_PATH=$(find /var/www/wayfarer/ChromeCache/playwright-browsers -name "chrome" -type f | head -n1)

# Test it
$CHROME_PATH --version
# Should output: Chromium version number
```

**6. Check for missing library dependencies:**

```bash
CHROME_PATH=$(find /var/www/wayfarer/ChromeCache/playwright-browsers -name "chrome" -type f | head -n1)
ldd $CHROME_PATH | grep "not found"
# Should show no output (all libraries found)
```

If you see "not found", install the missing libraries from step 1.

**7. Check application logs:**

```bash
# View detailed error messages
sudo journalctl -u wayfarer -n 100 | grep -i "chrome\|playwright"

# Or check log files
sudo tail -f /var/log/wayfarer/wayfarer-*.log | grep -i "playwright"
```

**8. Force reinstall Chromium:**

If Chromium download is corrupted:

```bash
# Delete cached browsers
sudo rm -rf /var/www/wayfarer/ChromeCache/playwright-browsers

# Reinstall
sudo -u wayfarer bash
cd /var/www/wayfarer
pwsh bin/Release/net9.0/playwright.ps1 install chromium
exit
```

**Common issues:**

- **Missing libraries:** System libraries not installed → Install dependencies from step 1
- **Permission issues:** `wayfarer` user can't write to `ChromeCache/` → Check permissions
- **Corrupted download:** Chromium download was interrupted → Delete and reinstall (step 8)
- **No disk space:** Chromium needs ~400MB → Free up disk space

**ARM64 Linux Notes:**

Playwright now provides **official ARM64 Linux Chromium binaries**! No more need for system Chromium or xtradeb PPA workarounds. PDF export works identically on ARM64 as on x64.

---

## Security Hardening / Rate Limiting / Fail2ban

After basic installation, harden your instance with **Nginx rate limiting** (fast, stateless) and **Fail2ban** (persistent bans). Use both for layered protection.

### Nginx Rate Limiting

Protect against brute force, scanners, and noisy clients at the web edge.

**Template:** `deployment/nginx-ratelimit.conf`

**What it includes**

- Rate-limit zones (general, login, API, 404)
- Per-IP connection limits
- Security headers (X-Frame-Options, X-Content-Type-Options, etc.)
- Immediate blocking of `*.php` probes / WordPress scanners
- Static file rules for better performance

**How to use**

```bash
# Option 1: Replace your entire server block
sudo cp deployment/nginx-ratelimit.conf /etc/nginx/sites-available/wayfarer

# Option 2: Integrate pieces
# - Copy the limit_req_zone blocks into nginx.conf’s http{} section
# - Apply the location rules inside your existing server{} block
```

**Customize before enabling**

1. Replace `yourdomain.com` with your real domain(s)
2. Update TLS certificate paths
3. Adjust limits to your expected traffic
4. Update Kestrel upstream port if not 5000
5. Confirm logs and wwwroot paths

**Apply & verify**

```bash
sudo nginx -t
sudo systemctl reload nginx
# Watch for limit hits:
sudo tail -f /var/log/nginx/error.log | grep -i limiting
```

---

### Fail2ban (Nginx access-log jails — recommended)

Fail2ban reads the **Nginx access log** and bans repeat offenders automatically. This works across all Wayfarer installs and correctly uses the real client IP (when Nginx is configured to see it).

**1) Ensure a dedicated access log**
Add this inside your Wayfarer server block (HTTP and HTTPS):

```nginx
access_log /var/log/nginx/wayfarer.access.log combined;
```

Then reload:

```bash
sudo nginx -t && sudo systemctl reload nginx
```

**2) Install Wayfarer filters & jails**

```bash
# Install filters (scanner / 404 storm / login abuse)
sudo cp deployment/fail2ban/wayfarer-nginx-*.conf /etc/fail2ban/filter.d/

# Install jails (uses /var/log/nginx/wayfarer.access.log)
sudo cp deployment/fail2ban/wayfarer-nginx.conf /etc/fail2ban/jail.d/

# Enable (or install fail2ban first: sudo apt install -y fail2ban)
sudo systemctl restart fail2ban
```

**3) Verify**

```bash
sudo fail2ban-client status
sudo fail2ban-client status wayfarer-nginx-scanner
sudo fail2ban-client status wayfarer-nginx-404
sudo fail2ban-client status wayfarer-nginx-login
```

**What gets detected**

- **Scanner:** PHP probes & known vuln paths (`/wp-admin`, `/xmlrpc.php`, `/.env`, etc.)
- **404 storm:** high rate of 404s per IP
- **Login abuse:** repeated hits to common login endpoints
  (covers `/Identity/Account/Login`, `/Account/Login`, `/signin`, `/auth/login`, `/login`)

**Tuning & safety**

```ini
# Tweak in /etc/fail2ban/jail.d/wayfarer-nginx.conf
maxretry = (attempts before ban)
findtime = (detection window, e.g., 10m)
bantime  = (ban duration, e.g., 1h or 12h)
```

Whitelist yourself (optional):

```bash
echo -e "[DEFAULT]\nignoreip = 127.0.0.1/8 ::1 <your-ip-or-cidr>" | sudo tee /etc/fail2ban/jail.d/ignoreip.local
sudo systemctl restart fail2ban
```

**Quick tests (from another machine)**

```bash
# Scanner (should trip wayfarer-nginx-scanner)
for i in {1..12}; do curl -skS https://yourdomain.com/test.php >/dev/null; done

# Login (should trip wayfarer-nginx-login)
for i in {1..12}; do curl -skS https://yourdomain.com/Identity/Account/Login >/dev/null; done

# 404 storm (should trip wayfarer-nginx-404)
for i in {1..45}; do curl -skS https://yourdomain.com/definitely-not-here-$i >/dev/null; done
```

**Notes**

- If you’re behind a proxy/CDN (Cloudflare/ELB), configure Nginx `real_ip_header` and `set_real_ip_from` so logs contain the **visitor’s** IP.
- Keep Nginx rate-limits **and** Fail2ban enabled—rate-limits throttle bursts; Fail2ban bans persistent offenders.

---

### Combined Protection

Use both layers:

1. **Nginx rate limiting** — instant, lightweight throttling
2. **Fail2ban** — longer bans for persistent hostile behavior

Together they mitigate:

- ✅ Brute-force login attempts
- ✅ Vulnerability scanners / PHP probes
- ✅ 404 probing & path discovery
- ✅ Bursty or abusive clients

---

## Security Checklist

After installation, ensure:

- [ ] Changed default admin password
- [ ] Database user has strong password
- [ ] HTTPS is enabled (Let's Encrypt)
- [ ] Firewall configured (only ports 80, 443, and SSH open)
- [ ] Application running as non-root user (`wayfarer`)
- [ ] Regular database backups scheduled (automated via cron or similar)
- [ ] `appsettings.json` has correct file permissions (not world-readable if it contains secrets)
- [ ] Monitoring configured for disk space usage (cache directories can grow large)
- [ ] **Nginx rate limiting configured** (using `deployment/nginx-ratelimit.conf`)
- [ ] **Fail2ban installed and configured** (using `deployment/fail2ban/wayfarer-nginx-*.conf` and `deployment/fail2ban/wayfarer-nginx.conf`)
- [ ] Fail2ban is actively monitoring logs (`systemctl status fail2ban`)

---

## Support & Documentation

- **GitHub Issues:** <https://github.com/yourusername/wayfarer/issues>
- **Developer Documentation:** See `docs/developer/` folder
- **User Documentation:** See `docs/user/` folder
- **Configuration Reference:** `docs/developer/3-Configuration.md`
- **API Documentation:** `docs/developer/10-API.md`

---

**Installation Complete!** Your Wayfarer instance should now be running and accessible at your domain.
