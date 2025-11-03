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

---

## Prerequisites

### Hardware Requirements

**Minimum:**
- 1 GB RAM
- 2 GB disk space (plus storage for uploaded data and logs)
- ARM or x64 CPU (Raspberry Pi 3+ or equivalent)

**Recommended:**
- 2+ GB RAM
- 10+ GB disk space
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

**Note:** For Ubuntu/Debian versions or ARM devices, see: https://learn.microsoft.com/en-us/dotnet/core/install/linux

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
    "TileCacheDirectory": "/var/www/wayfarer/TileCache"
  },
  "AllowedHosts": "*"
}
```

**Connection String Options:**

- **TCP Connection:** `Host=localhost;Port=5432;Database=wayfarer;Username=wayfareruser;Password=yourpassword`
- **Unix Socket:** `Host=/var/run/postgresql;Database=wayfarer;Username=wayfareruser;Password=yourpassword` (faster, local only)

### 2. Configure for Production (Optional)

Create `appsettings.Production.json` for production-specific settings:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Error",
      "Wayfarer.Middleware.PerformanceMonitoringMiddleware": "Warning"
    }
  }
}
```

Exit the wayfarer user shell:
```bash
exit
```

---

## Directory Structure & Permissions

### 1. Create Required Directories

```bash
# Create log directory
sudo mkdir -p /var/log/wayfarer
sudo chown wayfarer:wayfarer /var/log/wayfarer
sudo chmod 755 /var/log/wayfarer

# Create tile cache directory
sudo mkdir -p /var/www/wayfarer/TileCache
sudo chown wayfarer:wayfarer /var/www/wayfarer/TileCache
sudo chmod 755 /var/www/wayfarer/TileCache

# Create uploads directory (created automatically by app, but can pre-create)
sudo mkdir -p /var/www/wayfarer/Uploads
sudo chown wayfarer:wayfarer /var/www/wayfarer/Uploads
sudo chmod 755 /var/www/wayfarer/Uploads
```

### 2. Set Application Permissions

```bash
# Ensure wayfarer user owns the entire application
sudo chown -R wayfarer:wayfarer /var/www/wayfarer

# Set directory permissions
sudo find /var/www/wayfarer -type d -exec chmod 755 {} \;

# Set file permissions
sudo find /var/www/wayfarer -type f -exec chmod 644 {} \;
```

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
├── wwwroot/                         # Static files (CSS, JS, images)
├── TileCache/                       # Map tile cache (writable)
├── Uploads/                         # User uploads (writable)
└── ...

/var/log/wayfarer/
└── wayfarer-*.log                   # Application logs (writable)
```

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

```bash
sudo nano /etc/systemd/system/wayfarer.service
```

Paste the following:

```ini
[Unit]
Description=Wayfarer - Location Tracking and Trip Planning
After=network.target postgresql.service
Wants=postgresql.service

[Service]
Type=notify
User=wayfarer
Group=wayfarer
WorkingDirectory=/var/www/wayfarer
ExecStart=/usr/bin/dotnet /var/www/wayfarer/Wayfarer.dll
Restart=always
RestartSec=10
KillSignal=SIGINT
SyslogIdentifier=wayfarer
Environment=ASPNETCORE_ENVIRONMENT=Production
Environment=DOTNET_PRINT_TELEMETRY_MESSAGE=false

# Resource limits
LimitNOFILE=65536

[Install]
WantedBy=multi-user.target
```

### 2. Publish the Application

Before running as a service, publish the app for production:

```bash
# Switch to wayfarer user
sudo -u wayfarer bash
cd /var/www/wayfarer

# Publish the application
dotnet publish -c Release -o /var/www/wayfarer

# This creates optimized Wayfarer.dll
exit
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

### 6. Set Up Log Rotation (Optional)

Prevent logs from consuming too much disk space:

```bash
sudo nano /etc/logrotate.d/wayfarer
```

Add:

```
/var/log/wayfarer/*.log {
    daily
    rotate 14
    compress
    delaycompress
    notifempty
    missingok
    create 0644 wayfarer wayfarer
}
```

Test:
```bash
sudo logrotate -f /etc/logrotate.d/wayfarer
```

---

## Updating Wayfarer

### 1. Backup Database

```bash
# Backup database
sudo -u postgres pg_dump wayfarer > wayfarer-backup-$(date +%Y%m%d).sql

# Or backup to compressed file
sudo -u postgres pg_dump wayfarer | gzip > wayfarer-backup-$(date +%Y%m%d).sql.gz
```

### 2. Pull Latest Code

```bash
# Switch to wayfarer user
sudo -u wayfarer bash
cd /var/www/wayfarer

# Stash any local changes (if any)
git stash

# Pull latest changes
git pull origin main

# Or download specific release
# git fetch --tags
# git checkout v1.1.0

exit
```

### 3. Rebuild and Restart

```bash
# Stop the service
sudo systemctl stop wayfarer

# Switch to wayfarer user
sudo -u wayfarer bash
cd /var/www/wayfarer

# Restore dependencies and publish
dotnet publish -c Release -o /var/www/wayfarer

exit

# Start the service
sudo systemctl start wayfarer

# Check status
sudo systemctl status wayfarer

# Watch logs for any issues
sudo journalctl -u wayfarer -f
```

**Note:** The application automatically applies new database migrations on startup, so no manual migration commands are needed.

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
sudo du -sh /var/log/wayfarer/*
```

**Clean up:**
- Old logs: `sudo find /var/log/wayfarer -name "*.log" -mtime +30 -delete`
- Tile cache: Navigate to Admin → Settings → Clear Tile Cache
- Uploads: Review and delete old uploads through the web interface

---

## Security Checklist

After installation, ensure:

- [ ] Changed default admin password
- [ ] Database user has strong password
- [ ] HTTPS is enabled (Let's Encrypt)
- [ ] Firewall configured (only ports 80, 443, and SSH open)
- [ ] Application running as non-root user (`wayfarer`)
- [ ] Log rotation configured
- [ ] Regular database backups scheduled
- [ ] `appsettings.json` has correct file permissions (not world-readable if it contains secrets)

---

## Support & Documentation

- **GitHub Issues:** https://github.com/yourusername/wayfarer/issues
- **Developer Documentation:** See `docs/developer/` folder
- **User Documentation:** See `docs/user/` folder
- **Configuration Reference:** `docs/developer/3-Configuration.md`
- **API Documentation:** `docs/developer/10-API.md`

---

**Installation Complete!** Your Wayfarer instance should now be running and accessible at your domain.
