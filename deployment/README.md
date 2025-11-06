# Wayfarer Deployment Files

This directory contains ready-to-use configuration files and scripts for deploying Wayfarer in production.

## Files Overview

### Deployment Automation

**`deploy.sh`** - Automated deployment script
- Pulls code from Git
- Builds the application
- Applies database migrations
- Deploys to production directory
- Handles permissions automatically
- Preserves user data directories

**Usage:**
```bash
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

**`nginx-ratelimit.conf`** - Complete nginx reverse proxy with security
- Rate limiting (login, API, general traffic)
- Security headers
- Static file serving
- SSL/TLS configuration
- Blocks common exploit attempts

**Installation:** See file header for detailed instructions

---

### Fail2ban Protection

**`fail2ban-wayfarer-filter.conf`** - Detection patterns for malicious activity
- PHP file scanners
- WordPress vulnerability probes
- Repeated 404 errors
- Failed login attempts

**`fail2ban-wayfarer-jail.conf`** - Ban policies and configuration
- Scanner bot protection
- Brute force login protection
- Configurable thresholds

**Installation:**
```bash
sudo cp deployment/fail2ban-wayfarer-filter.conf /etc/fail2ban/filter.d/wayfarer.conf
# Edit jail config to set your log path, then add to jail.local
sudo systemctl restart fail2ban
```

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

## Customization Required

All files use **placeholders** that must be customized for your deployment:

| File | What to Customize |
|------|-------------------|
| `deploy.sh` | `APP_DIR`, `DEPLOY_DIR`, `APP_USER`, `SERVICE_NAME` |
| `wayfarer.service` | `User`, `WorkingDirectory`, port in `--urls` |
| `nginx-ratelimit.conf` | Domain name, SSL paths, Kestrel port, log paths |
| `fail2ban-wayfarer-jail.conf` | `logpath` (must match your actual log location) |

---

## Documentation

For complete installation and deployment guide, see:
- **[docs/developer/13-Deployment.md](../docs/developer/13-Deployment.md)** - Full deployment guide
- **[docs/user/2-Install-and-Dependencies.md](../docs/user/2-Install-and-Dependencies.md)** - Quick start guide

---

## Support

- **Documentation:** See `docs/` directory
- **Issues:** Open an issue on GitHub
- **Security:** See deployment guide for hardening checklist

---

**Note:** All configuration files are templates designed for open-source deployment. Always review and customize for your specific environment before using in production.
