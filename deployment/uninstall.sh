#!/usr/bin/env bash
set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

NONINTERACTIVE=0
PURGE_DB=0

for arg in "$@"; do
  case "$arg" in
    --non-interactive|-y)
      NONINTERACTIVE=1
      ;;
    --purge-db)
      PURGE_DB=1
      ;;
  esac
done

# Defaults (should match install.sh)
APP_USER="${APP_USER:-wayfarer}"
DEPLOY_DIR="${DEPLOY_DIR:-/var/www/wayfarer}"
SERVICE_NAME="${SERVICE_NAME:-wayfarer}"

DB_NAME="${DB_NAME:-wayfarer}"
DB_USER="${DB_USER:-wayfarer_user}"

# Certbot: primary certificate name/domain (for delete)
CERTBOT_DOMAIN="${CERTBOT_DOMAIN:-}"   # e.g. wayfarer.example.com

NGINX_RATELIMIT_DST="/etc/nginx/conf.d/nginx-ratelimit.conf"
NGINX_VHOST_DST="/etc/nginx/sites-available/wayfarer.conf"
NGINX_VHOST_LINK="/etc/nginx/sites-enabled/wayfarer.conf"

confirm() {
  local msg="$1"
  if [[ $NONINTERACTIVE -eq 1 ]]; then
    return 0
  fi
  read -rp "$msg [y/N]: " A
  A="${A:-N}"
  [[ "$A" =~ ^[Yy]$ ]]
}

echo "========================================="
echo " Wayfarer Uninstaller"
echo "========================================="
echo ""
echo "This will attempt to remove:"
echo "  - systemd service for Wayfarer"
echo "  - deployment directory: $DEPLOY_DIR"
echo "  - Nginx vhost + rate-limit config (if installed)"
echo "  - Fail2ban jails/filters named 'wayfarer-nginx-*'"
echo "Optionally: drop PostgreSQL DB + user."
echo ""

if [[ $NONINTERACTIVE -eq 0 ]]; then
  if ! confirm "Proceed with uninstall?"; then
    echo "Aborted."
    exit 0
  fi
fi

# ------------------------------
# 1. Stop & disable systemd service
# ------------------------------
echo ""
echo "Stopping and disabling systemd service (if present)..."
if systemctl list-unit-files | grep -q "^${SERVICE_NAME}.service"; then
  sudo systemctl stop "$SERVICE_NAME" || true
  sudo systemctl disable "$SERVICE_NAME" || true
  sudo rm -f "/etc/systemd/system/${SERVICE_NAME}.service"
  sudo systemctl daemon-reload
else
  echo "Service ${SERVICE_NAME}.service not found; skipping."
fi

# ------------------------------
# 2. Remove deployment directory
# ------------------------------
echo ""
echo "Removing deployment directory: $DEPLOY_DIR"
sudo rm -rf "$DEPLOY_DIR"

# ------------------------------
# 3. Nginx cleanup
# ------------------------------
echo ""
echo "Cleaning up Nginx config (if present)..."

if [[ -L "$NGINX_VHOST_LINK" || -f "$NGINX_VHOST_LINK" ]]; then
  sudo rm -f "$NGINX_VHOST_LINK"
fi

if [[ -f "$NGINX_VHOST_DST" ]]; then
  sudo rm -f "$NGINX_VHOST_DST"
fi

if [[ -f "$NGINX_RATELIMIT_DST" ]]; then
  sudo rm -f "$NGINX_RATELIMIT_DST"
fi

if command -v nginx >/dev/null 2>&1; then
  sudo nginx -t || true
  sudo systemctl reload nginx || true
fi

# ------------------------------
# 4. Fail2ban cleanup
# ------------------------------
echo ""
echo "Cleaning up Fail2ban jails/filters (if present)..."

if command -v fail2ban-server >/dev/null 2>&1; then
  sudo rm -f /etc/fail2ban/jail.d/wayfarer-nginx.conf || true
  sudo rm -f /etc/fail2ban/filter.d/wayfarer-nginx-404.conf || true
  sudo rm -f /etc/fail2ban/filter.d/wayfarer-nginx-login.conf || true
  sudo rm -f /etc/fail2ban/filter.d/wayfarer-nginx-scanner.conf || true
  sudo systemctl restart fail2ban || true
else
  echo "Fail2ban not installed; skipping."
fi

# ------------------------------
# 5. PostgreSQL cleanup (optional)
# ------------------------------
echo ""
echo "PostgreSQL cleanup: database '${DB_NAME}' and user '${DB_USER}'."

if [[ $PURGE_DB -eq 1 ]]; then
  DO_DB=1
elif [[ $NONINTERACTIVE -eq 1 ]]; then
  DO_DB=0
else
  if confirm "Drop database '${DB_NAME}' and user '${DB_USER}'?"; then
    DO_DB=1
  else
    DO_DB=0
  fi
fi

if [[ $DO_DB -eq 1 ]]; then
  echo "Dropping database and user..."
  sudo -u postgres psql -c "DROP DATABASE IF EXISTS ${DB_NAME};" || true
  sudo -u postgres psql -c "DROP USER IF EXISTS ${DB_USER};" || true
else
  echo "Leaving database and user intact."
fi

# ------------------------------
# 7. Certbot / Let's Encrypt cleanup (optional)
# ------------------------------
echo ""
echo "Certbot / Let's Encrypt cleanup (optional)."

if ! command -v certbot >/dev/null 2>&1; then
  echo "Certbot is not installed; skipping certificate removal."
else
  # Decide whether to delete cert
  DO_CERT=0

  if [[ $NONINTERACTIVE -eq 1 ]]; then
    # Non-interactive: user must explicitly provide CERTBOT_DOMAIN and maybe PURGE_CERT=1 if you want
    if [[ -n "$CERTBOT_DOMAIN" && "$PURGE_CERT" == "1" ]]; then
      DO_CERT=1
    fi
  else
    if confirm "Delete Let's Encrypt certificate managed by Certbot as well?"; then
      DO_CERT=1
    fi
  fi

  if [[ $DO_CERT -eq 1 ]]; then
    if [[ -z "$CERTBOT_DOMAIN" ]]; then
      if [[ $NONINTERACTIVE -eq 1 ]]; then
        echo "CERTBOT_DOMAIN not set; skipping certificate delete."
      else
        read -rp "Enter the primary certificate name (usually the domain, e.g. wayfarer.example.com): " CERTBOT_DOMAIN
      fi
    fi

    if [[ -n "$CERTBOT_DOMAIN" ]]; then
      echo "Deleting certificate '$CERTBOT_DOMAIN' via Certbot..."
      sudo certbot delete --cert-name "$CERTBOT_DOMAIN" -n || true
    else
      echo "No certificate name provided; skipping Certbot delete."
    fi
  else
    echo "Leaving Certbot certificate data intact."
  fi
fi

# ------------------------------
# 8. Final info
# ------------------------------
echo ""
echo "========================================="
echo " Uninstall complete"
echo "========================================="
echo ""
echo "What remains NOT touched:"
echo "  - Wayfarer Git repository (source) at: $SCRIPT_DIR"
echo "  - System packages: PostgreSQL, Nginx, dotnet, fail2ban (if installed)"
echo ""
echo "You can remove the repo directory manually if you wish:"
echo "  rm -rf \"$SCRIPT_DIR\""
