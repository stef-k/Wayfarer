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

### 6. Install Chrome Dependencies (for PDF Export)

Wayfarer uses Puppeteer to generate PDF exports of trips. Puppeteer automatically downloads Chrome, but requires system libraries:

```bash
# Debian/Ubuntu
sudo apt update && sudo apt install -y \
  libnss3 \
  libnspr4 \
  libatk1.0-0t64 \
  libatk-bridge2.0-0t64 \
  libcups2t64 \
  libdrm2 \
  libdbus-1-3 \
  libxkbcommon0 \
  libxcomposite1 \
  libxdamage1 \
  libxfixes3 \
  libxrandr2 \
  libgbm1 \
  libasound2t64 \
  libpango-1.0-0 \
  libcairo2
```

**Note:** Chrome will be automatically downloaded by PuppeteerSharp on first PDF export. See [PDF Export Troubleshooting](#pdf-export--chrome-issues) if you encounter errors.

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

**Note:** ChromeCache stores the Chromium browser used for PDF export. It's automatically downloaded (130MB) when you first export a trip as PDF. This directory is preserved during updates.

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

Paste the following configuration:

```nginx
# Redirect www to non-www (optional)
server {
    listen 80;
    server_name www.yourdomain.com;
    return 301 http://yourdomain.com$request_uri;
}

# Main server configuration
server {
    listen 80;
    server_name yourdomain.com;

    # Increase max upload size (adjust as needed)
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

    # Proxy all other requests (SSE-compatible for real-time updates)
    location / {
        proxy_pass         http://localhost:5000;
        proxy_http_version 1.1;
        proxy_set_header   Host $host;
        proxy_cache_bypass $http_upgrade;
        proxy_set_header   X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header   X-Forwarded-Proto $scheme;
        proxy_set_header   X-Forwarded-Host $host;

        # Server-Sent Events (SSE) configuration
        proxy_buffering     off;
        proxy_cache         off;
        proxy_read_timeout  3600s;
        proxy_send_timeout  3600s;
        send_timeout        3600s;
    }

    # Security headers
    add_header X-Content-Type-Options "nosniff" always;
    add_header Referrer-Policy "no-referrer-when-downgrade" always;

    # Allow embedding for public sharing (adjust if needed)
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
Type=notify
User=wayfarer
WorkingDirectory=/var/www/wayfarer
ExecStart=/usr/bin/dotnet /var/www/wayfarer/Wayfarer.dll --urls http://localhost:5000
Restart=always
Environment=ASPNETCORE_ENVIRONMENT=Production

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

### PDF Export / Chrome Issues

**Symptom:** Error when exporting trips to PDF:

```
System.ComponentModel.Win32Exception (8): An error occurred trying to start process
'/path/to/chrome' with working directory '/path/to/app'. Exec format error
```

**How Wayfarer handles Chrome:**

- Wayfarer uses **PuppeteerSharp** to generate PDFs
- Chrome is **NOT** included in the repository
- Chrome is automatically downloaded to `~/.local/share/puppeteer/` on first PDF export
- The application user (`wayfarer`) must have write permissions to this directory

**Troubleshooting steps:**

**1. Check if Chrome dependencies are installed:**

```bash
# Verify libraries are present
dpkg -l | grep -E 'libnss3|libgbm1|libasound2'
```

If missing, install them (see [Install Dependencies](#install-dependencies) section).

**2. Check Chrome download location:**

```bash
# Switch to wayfarer user
sudo -u wayfarer bash

# Check if Chrome was downloaded
ls -la ~/.local/share/puppeteer/
# Should show Chrome directory like: Chrome-Linux-138.0.7204.92/

exit
```

**3. If Chrome is missing or corrupted, delete and re-download:**

```bash
sudo -u wayfarer bash
rm -rf ~/.local/share/puppeteer/
exit

# Trigger a PDF export from the web interface to download Chrome
```

**4. Check Chrome binary has execute permission:**

```bash
sudo -u wayfarer bash
chmod +x ~/.local/share/puppeteer/*/chrome-linux*/chrome
exit
```

**5. Test Chrome binary manually:**

```bash
sudo -u wayfarer bash
~/.local/share/puppeteer/*/chrome-linux*/chrome --version
# Should output: Chrome version number
exit
```

**6. Check for missing library dependencies:**

```bash
sudo -u wayfarer bash
ldd ~/.local/share/puppeteer/*/chrome-linux*/chrome | grep "not found"
# Should show no output (all libraries found)
exit
```

If you see "not found", install the missing libraries.

**7. Check application logs:**

```bash
# View detailed error messages
sudo journalctl -u wayfarer -n 100 | grep -i "chrome\|puppeteer"

# Or check log files
sudo tail -f /var/log/wayfarer/wayfarer-*.log | grep -i "chrome"
```

**Common causes:**

- **Wrong architecture:** Chrome binary doesn't match CPU (x86 vs ARM) → Delete and re-download
- **Missing libraries:** System libraries not installed → Install dependencies from step 1
- **Permission issues:** `wayfarer` user can't write to `~/.local/share/` → Check permissions
- **Corrupted download:** Chrome download was interrupted → Delete and re-download

**Alternative: Use system-installed Chrome**

If automatic download doesn't work, install Chrome system-wide:

```bash
# Install Chrome
wget -q -O - https://dl-ssl.google.com/linux/linux_signing_key.pub | sudo apt-key add -
echo "deb [arch=amd64] http://dl.google.com/linux/chrome/deb/ stable main" | sudo tee /etc/apt/sources.list.d/google-chrome.list
sudo apt update
sudo apt install -y google-chrome-stable

# Test installation
google-chrome --version
```

Then modify `Services/TripExportService.cs` and `Services/MapSnapshotService.cs` to use system Chrome by adding `ExecutablePath`:

```csharp
await using var browser = await Puppeteer.LaunchAsync(new LaunchOptions
{
    Headless = true,
    ExecutablePath = "/usr/bin/google-chrome"  // Add this line
});
```

---

## Security Hardening / Rate Limiting / Fail2ban

After basic installation, enhance your security with rate limiting and intrusion detection.

### Nginx Rate Limiting

Protect against brute force attacks, DDoS, and scanner bots with rate limiting.

**Complete configuration template:** `deployment/nginx-ratelimit.conf`

This template includes:

- **Rate limit zones** for general traffic, login pages, API endpoints, and 404 errors
- **Connection limits** per IP address
- **Security headers** (X-Frame-Options, X-Content-Type-Options, etc.)
- **Automatic blocking** of .php requests and WordPress scanners at nginx level
- **Static file serving** for better performance

**To use the rate limiting config:**

```bash
# Option 1: Replace your entire nginx config
sudo cp deployment/nginx-ratelimit.conf /etc/nginx/sites-available/wayfarer

# Option 2: Include rate limit zones in your existing config
# Add the rate limit zones from the template to your http {} block
# Then apply the location-specific limits in your server {} block
```

**Customize before using:**

1. Replace `yourdomain.com` with your actual domain
2. Update SSL certificate paths
3. Adjust rate limits based on your traffic patterns
4. Update Kestrel port if not using default 5000
5. Set correct paths for logs and wwwroot

**Test configuration:**

```bash
sudo nginx -t
sudo systemctl reload nginx
```

**Monitor rate limiting:**

```bash
# Watch for rate limit triggers
sudo tail -f /var/log/nginx/error.log | grep "limiting"
```

### Fail2ban - Intrusion Detection

Block repeat offenders automatically with fail2ban.

**Configuration files provided:**

- `deployment/fail2ban-wayfarer-filter.conf` - Detection patterns
- `deployment/fail2ban-wayfarer-jail.conf` - Jail configuration

**What gets detected and blocked:**

- Multiple requests for .php files (scanner bots)
- WordPress vulnerability probes (wp-admin, wp-content)
- Repeated 404 errors
- Failed login attempts

**Installation:**

```bash
# 1. Install fail2ban if not already installed
sudo apt install -y fail2ban

# 2. Copy filter configuration
sudo cp deployment/fail2ban-wayfarer-filter.conf /etc/fail2ban/filter.d/wayfarer.conf

# 3. Add jail configuration
sudo nano /etc/fail2ban/jail.local
# Copy content from deployment/fail2ban-wayfarer-jail.conf
# IMPORTANT: Update logpath to your actual log location
```

**Customize jail.local:**

- **logpath**: Update to your actual Wayfarer log path
  - Common: `/var/log/wayfarer/wayfarer-$(date +%Y%m%d).log`
  - Or check your `appsettings.json` for log location
- **maxretry**: Number of violations before ban (default: 10 for scanners, 5 for logins)
- **findtime**: Time window in seconds (default: 600 = 10 minutes)
- **bantime**: How long to ban in seconds (default: 3600 = 1 hour)

**Enable and start:**

```bash
# Restart fail2ban to load new config
sudo systemctl restart fail2ban

# Check status
sudo fail2ban-client status

# Check Wayfarer jails specifically
sudo fail2ban-client status wayfarer-scanner
sudo fail2ban-client status wayfarer-login
```

**Monitor fail2ban:**

```bash
# View banned IPs
sudo fail2ban-client status wayfarer-scanner

# View fail2ban log
sudo tail -f /var/log/fail2ban.log

# Manually unban an IP if needed
sudo fail2ban-client set wayfarer-scanner unbanip 192.168.1.100
```

**Testing fail2ban (optional):**

```bash
# From another machine, trigger the filter:
# Request .php files multiple times
for i in {1..12}; do curl http://yourdomain.com/test.php; done

# Check if your IP got banned
sudo fail2ban-client status wayfarer-scanner
```

### Combined Protection

Using both nginx rate limiting AND fail2ban provides layered security:

1. **Nginx rate limiting** - First line of defense, instant blocking, no database needed
2. **Fail2ban** - Long-term bans for persistent offenders, system-wide protection

Together they protect against:

- ✅ Brute force login attempts
- ✅ Scanner bots looking for vulnerabilities
- ✅ DDoS attempts
- ✅ Automated attacks
- ✅ Repeated 404 probing

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
- [ ] **Fail2ban installed and configured** (using `deployment/fail2ban-*.conf`)
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
