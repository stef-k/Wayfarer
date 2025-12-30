# Wayfarer Deployment Files

This directory contains ready-to-use configuration files and scripts for deploying Wayfarer in production.

## Files Overview

### Deployment Automation

**`install.sh`** – Interactive installer

- Installs system packages (PostgreSQL + PostGIS, Nginx, runtime libs)
- Creates DB + user and enables PostGIS/citext
- Creates deployment directory and app user
- Installs systemd service, Nginx vhost, Fail2ban jails
- Optionally configures HTTPS via Certbot
- Can run `deploy.sh` for the first deployment

**`deploy.sh`** – Automated deployment script

- Pulls code from Git
- Builds the application
- Applies database migrations
- Deploys to the production directory
- Handles permissions automatically
- Preserves user data directories

**`uninstall.sh`** – Clean removal script

- Stops and disables the systemd service
- Removes systemd unit, Nginx vhost, Fail2ban jails
- Optionally drops the Wayfarer DB and user (`--purge-db`)
- Optionally deletes the Certbot certificate

**Usage:**

### First-time install (fresh server)

```bash
# 1. Clone the repo
git clone https://github.com/yourusername/Wayfarer.git
cd Wayfarer/deployment

# 2. Make scripts executable
chmod +x install.sh deploy.sh uninstall.sh

# 3. Run interactive installer (recommended)
./install.sh


# Deploy from master
./deployment/deploy.sh

# Deploy specific branch/tag
REF=v1.2.0 ./deployment/deploy.sh
```

---

### Systemd Service

**`wayfarer.service`** - Systemd service configuration

- Runs Wayfarer as a system service
- Auto-restart on failure
- Starts on system boot
- Fully documented with customization points

**Installation:**

```bash
sudo cp deployment/wayfarer.service /etc/systemd/system/
sudo systemctl daemon-reload
sudo systemctl enable wayfarer
sudo systemctl start wayfarer
```

---

### Nginx Configuration

**`nginx-ratelimit.conf`** – Global rate-limit configuration

- Defines `limit_req_zone` and `limit_conn_zone` contexts
- Zones used by the Wayfarer vhost (wayfarer_general, wayfarer_login, wayfarer_api, wayfarer_404, wayfarer_conn)
- Installed to `/etc/nginx/conf.d/nginx-ratelimit.conf` by `install.sh`

**`wayfarer-nginx-vhost.conf`** – Wayfarer reverse proxy

- HTTP → HTTPS redirect
- SSL/TLS configuration (Let’s Encrypt compatible)
- Security headers
- Static file serving from `/var/www/wayfarer/wwwroot`
- Proxies to Kestrel on `http://localhost:5000`
- Uses the rate-limit zones defined in `nginx-ratelimit.conf`


**Installation:** See file header for detailed instructions

---

### Fail2ban Protection

**`wayfarer-nginx.conf`** – Fail2ban jail configuration

- Scanner bot protection
- Brute-force login protection
- 404 flood protection
- Uses `/var/log/nginx/wayfarer-access.log` as log source (customize if needed)

**`wayfarer-nginx-404.conf`**, **`wayfarer-nginx-login.conf`**, **`wayfarer-nginx-scanner.conf`** – Filter definitions

- Regex patterns for 404 floods, login failures, and generic scanner activity
- Scanner bot protection
- Brute force login protection
- Configurable thresholds

**Installation (manual):**

```bash
sudo cp deployment/wayfarer-nginx.conf /etc/fail2ban/jail.d/wayfarer-nginx.conf
sudo cp deployment/wayfarer-nginx-404.conf /etc/fail2ban/filter.d/wayfarer-nginx-404.conf
sudo cp deployment/wayfarer-nginx-login.conf /etc/fail2ban/filter.d/wayfarer-nginx-login.conf
sudo cp deployment/wayfarer-nginx-scanner.conf /etc/fail2ban/filter.d/wayfarer-nginx-scanner.conf
sudo systemctl restart fail2ban

---

## Complete Deployment Workflow

### First-Time Setup

1. **Clone repository:**

   ```bash
   cd /home/youruser
   git clone https://github.com/yourusername/wayfarer.git Wayfarer
   ```

2. **Setup systemd service:**

   ```bash
   sudo cp Wayfarer/deployment/wayfarer.service /etc/systemd/system/
   sudo nano /etc/systemd/system/wayfarer.service  # Customize if needed
   sudo systemctl daemon-reload
   sudo systemctl enable wayfarer
   ```

3. **Configure deployment script:**

   ```bash
   cd Wayfarer
   chmod +x deployment/deploy.sh
   nano deployment/deploy.sh  # Set APP_DIR, DEPLOY_DIR, etc.
   ```

4. **Run first deployment:**

   ```bash
   ./deployment/deploy.sh
   ```

5. **Setup nginx (optional but recommended):**

   ```bash
   # Edit and customize first
   nano deployment/nginx-ratelimit.conf
   sudo cp deployment/nginx-ratelimit.conf /etc/nginx/sites-available/wayfarer
   sudo ln -s /etc/nginx/sites-available/wayfarer /etc/nginx/sites-enabled/
   sudo nginx -t
   sudo systemctl reload nginx
   ```

6. **Setup fail2ban (optional but recommended):**

   ```bash
   sudo cp deployment/fail2ban-wayfarer-filter.conf /etc/fail2ban/filter.d/wayfarer.conf
   # Edit jail config to set correct log path
   nano deployment/fail2ban-wayfarer-jail.conf
   # Add to /etc/fail2ban/jail.local
   sudo systemctl restart fail2ban
   ```

   ### HTTPS / Certbot

- `install.sh` can optionally install `certbot` + `python3-certbot-nginx` and request a Let’s Encrypt certificate for your domain.
- `uninstall.sh` can optionally delete the Certbot certificate (interactive or via `CERTBOT_DOMAIN` + `PURGE_CERT=1`).

### Updating Wayfarer

Simply run the deployment script:

```bash
cd /home/youruser/Wayfarer
./deployment/deploy.sh
```

Or deploy a specific version:

```bash
REF=v1.3.0 ./deployment/deploy.sh
```

---

## Secrets Management

**Database credentials are configured via systemd environment variables**, not in appsettings.json files.

The `appsettings.json` files contain **placeholder passwords** (`CHANGE_ME_BEFORE_DEPLOY`). These are overridden at runtime by the systemd service configuration.

### How It Works

1. **appsettings.json** (committed to repo) contains placeholder:
   ```json
   "DefaultConnection": "Host=localhost;...;Password=CHANGE_ME_BEFORE_DEPLOY"
   ```

2. **wayfarer.service** (on production server) contains real credentials:
   ```ini
   Environment="ConnectionStrings__DefaultConnection=Host=localhost;Database=wayfarer;Username=wayfarer_user;Password=REAL_SECRET"
   ```

3. ASP.NET Core automatically uses the environment variable, ignoring the JSON placeholder.

### Automatic Configuration

The `install.sh` script automatically:
- Prompts for database password during installation
- Writes the connection string to the systemd service file
- Reloads systemd to apply changes

The `deploy.sh` script automatically:
- Reads the connection string from the systemd service file before running migrations
- No need to manually export environment variables

### Manual Configuration

If configuring manually, add this line to `/etc/systemd/system/wayfarer.service` under `[Service]`:

```ini
Environment="ConnectionStrings__DefaultConnection=Host=localhost;Database=wayfarer;Username=wayfarer_user;Password=YOUR_SECURE_PASSWORD"
```

Then reload: `sudo systemctl daemon-reload && sudo systemctl restart wayfarer`

---

## Customization Required

All files use **placeholders** that must be customized for your deployment:

| File | What to Customize |
|------|-------------------|
| `deploy.sh` | `APP_DIR`, `DEPLOY_DIR`, `APP_USER`, `SERVICE_NAME` |
| `wayfarer.service` | `User`, `WorkingDirectory`, port in `--urls`, **connection string** |
| `nginx-ratelimit.conf` | Domain name, SSL paths, Kestrel port, log paths |
| `fail2ban-wayfarer-jail.conf` | `logpath` (must match your actual log location) |

---

## Documentation

For complete installation and deployment guide, see:

- **[Deployment Guide](26-Deployment.md)** - Full deployment guide
- **[Quick Start](02-Install-and-Dependencies.md)** - Quick start guide

---

## Support

- **Documentation:** See `docs/` directory
- **Issues:** Open an issue on GitHub
- **Security:** See deployment guide for hardening checklist

---

**Note:** All configuration files are templates designed for open-source deployment. Always review and customize for your specific environment before using in production.
