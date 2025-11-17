#!/usr/bin/env bash
set -e

#############################################
# Wayfarer Interactive Installer
# - Debian/Ubuntu (apt-based)
# - Supports interactive and non-interactive modes
#############################################

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# ------------------------------
# CLI flags / non-interactive
# ------------------------------
NONINTERACTIVE=0
AUTO_UNINSTALL_ON_FAIL=0

for arg in "$@"; do
  case "$arg" in
    --non-interactive|-y)
      NONINTERACTIVE=1
      ;;
    --auto-uninstall-on-fail)
      AUTO_UNINSTALL_ON_FAIL=1
      ;;
  esac
done

# ------------------------------
# Defaults (env override allowed)
# ------------------------------
APP_USER="${APP_USER:-wayfarer}"
DEPLOY_DIR="${DEPLOY_DIR:-/var/www/wayfarer}"
SERVICE_NAME="${SERVICE_NAME:-wayfarer}"
REF="${REF:-master}"

DB_NAME="${DB_NAME:-wayfarer}"
DB_USER="${DB_USER:-wayfarer_user}"
DB_PASS="${DB_PASS:-}"

DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Production}"

# Nginx config sources (if present)
NGINX_RATELIMIT_SRC="${NGINX_RATELIMIT_SRC:-$SCRIPT_DIR/nginx-ratelimit.conf}"
NGINX_VHOST_SRC="${NGINX_VHOST_SRC:-$SCRIPT_DIR/wayfarer-nginx-vhost.conf}"

# Certbot / HTTPS
CERTBOT_ENABLE="${CERTBOT_ENABLE:-0}"       # 0/1; can override via env
CERTBOT_STAGING="${CERTBOT_STAGING:-0}"     # 0 = real LE, 1 = staging
CERTBOT_DOMAIN="${CERTBOT_DOMAIN:-}"        # e.g. wayfarer.example.com
CERTBOT_EMAIL="${CERTBOT_EMAIL:-}"          # email for Let's Encrypt

# ------------------------------
# Error handler with optional rollback
# ------------------------------
on_error() {
  local exit_code=$?
  echo ""
  echo "✖ Installation failed (exit code: $exit_code)."

  if [[ $AUTO_UNINSTALL_ON_FAIL -eq 1 && -x "$SCRIPT_DIR/uninstall.sh" ]]; then
    if [[ $NONINTERACTIVE -eq 1 ]]; then
      echo "Running uninstall.sh in non-interactive mode to rollback changes..."
      "$SCRIPT_DIR/uninstall.sh" --non-interactive || true
    else
      echo ""
      echo "The installer can run uninstall.sh to rollback what it created."
      read -rp "Run uninstall.sh now? [y/N]: " R
      R="${R:-N}"
      if [[ "$R" =~ ^[Yy]$ ]]; then
        "$SCRIPT_DIR/uninstall.sh" || true
      else
        echo "Leaving partial installation in place. You can manually run:"
        echo "  ./uninstall.sh"
      fi
    fi
  else
    echo "You can run './uninstall.sh' later to remove Wayfarer-related files."
  fi

  exit "$exit_code"
}
trap on_error ERR

# ------------------------------
# Helpers
# ------------------------------
prompt_default() {
  local var_name="$1"
  local prompt="$2"
  local default="$3"

  # If variable already set (env), keep it
  if [[ -n "${!var_name}" ]]; then
    echo "$prompt [${!var_name}] (from env)"
    return
  fi

  if [[ $NONINTERACTIVE -eq 1 ]]; then
    printf -v "$var_name" "%s" "$default"
    echo "$prompt [$default] (non-interactive, using default)"
  else
    echo "$prompt"
    read -rp "[$default]: " value
    value="${value:-$default}"
    printf -v "$var_name" "%s" "$value"
  fi
}

prompt_password() {
  local var_name="$1"
  local explanation="$2"

  if [[ -n "${!var_name}" ]]; then
    echo "$explanation"
    echo "(Using password from environment variable $var_name)"
    return
  fi

  if [[ $NONINTERACTIVE -eq 1 ]]; then
    echo "$explanation"
    echo "✖ Non-interactive mode requires $var_name to be set."
    exit 1
  fi

  echo "$explanation"
  while true; do
    read -rsp "Password (input hidden): " pass1
    echo ""
    read -rsp "Confirm password: " pass2
    echo ""
    if [[ -z "$pass1" ]]; then
      echo "Password cannot be empty. Please try again."
    elif [[ "$pass1" != "$pass2" ]]; then
      echo "Passwords do not match. Please try again."
    else
      printf -v "$var_name" "%s" "$pass1"
      return
    fi
  done
}

require_apt() {
  if ! command -v apt-get >/dev/null 2>&1; then
    echo "✖ This installer currently supports apt-based systems (Debian/Ubuntu)."
    exit 1
  fi
}

require_sudo() {
  if ! command -v sudo >/dev/null 2>&1; then
    echo "✖ 'sudo' is required to run this installer."
    exit 1
  fi
}

confirm_or_exit() {
  if [[ $NONINTERACTIVE -eq 1 ]]; then
    return
  fi
  read -rp "Continue? [Y/n]: " C
  C="${C:-Y}"
  if [[ ! "$C" =~ ^[Yy]$ ]]; then
    echo "Aborted by user."
    exit 0
  fi
}

prompt_yes_no() {
  local var_name="$1"
  local prompt="$2"
  local default="$3"  # Y or N

  local current="${!var_name}"

  if [[ -n "$current" ]]; then
    echo "$prompt [$current] (from env)"
    return
  fi

  if [[ $NONINTERACTIVE -eq 1 ]]; then
    # Default is applied automatically in non-interactive mode
    printf -v "$var_name" "%s" "$default"
    echo "$prompt [$default] (non-interactive, using default)"
  else
    read -rp "$prompt [$default]: " ans
    ans="${ans:-$default}"
    printf -v "$var_name" "%s" "$ans"
  fi
}

# ------------------------------
# Banner
# ------------------------------
echo "========================================="
echo " Wayfarer Interactive Installer"
echo "========================================="
echo ""
echo "This script will:"
echo "  - Install core dependencies (PostgreSQL, PostGIS, Nginx, libs)"
echo "  - Create a PostgreSQL DB + user (with PostGIS + citext)"
echo "  - Create a deployment directory and app user"
echo "  - Install systemd service, Nginx config, Fail2ban rules (if present)"
echo "  - Run ./deploy.sh for the initial deployment"
echo ""

require_apt
require_sudo

if [[ $NONINTERACTIVE -eq 0 ]]; then
  confirm_or_exit
fi

# ------------------------------
# 1. Collect basic settings
# ------------------------------
CURRENT_USER="$(id -un)"

echo ""
echo "We need to know which Linux user account should own and run the Wayfarer app."
echo "This user will own the deployment directory and be used by systemd."
echo "You can use your current user, or create a dedicated 'wayfarer' user."

prompt_default "APP_USER" "Linux app user to run Wayfarer" "wayfarer"

echo ""
echo "We also need a directory where the published Wayfarer app will live."
echo "This directory is referenced by the systemd service."
prompt_default "DEPLOY_DIR" "Deployment directory for Wayfarer" "/var/www/wayfarer"

echo ""
echo "The installer assumes the Wayfarer repo lives in the same directory as this script."
echo "If you want a different location, move the repo and re-run the installer."
APP_DIR="$SCRIPT_DIR"
echo "App source directory: $APP_DIR"

echo ""
echo "We need the systemd service name. Usually 'wayfarer'."
prompt_default "SERVICE_NAME" "Systemd service name" "wayfarer"

echo ""
echo "We need to know which Git ref (branch or tag) to deploy."
prompt_default "REF" "Git ref to deploy (branch or tag)" "master"

# ------------------------------
# 2. Database configuration
# ------------------------------
echo ""
echo "Wayfarer uses PostgreSQL with PostGIS + citext."
echo "We'll create a dedicated database and user for Wayfarer."

prompt_default "DB_NAME" "PostgreSQL database name" "wayfarer"
prompt_default "DB_USER" "PostgreSQL username for Wayfarer" "wayfarer_user"

prompt_password "DB_PASS" \
"Now we need a password for the PostgreSQL user '${DB_USER}'."

# ------------------------------
# 3. Install system packages
# ------------------------------
echo ""
echo "========================================="
echo " Installing system packages (apt)"
echo "========================================="

echo "Updating package index..."
sudo apt-get update -y

echo ""
echo "Installing PostgreSQL + PostGIS..."
sudo apt-get install -y postgresql postgresql-contrib postgis

echo ""
echo "Installing Nginx..."
sudo apt-get install -y nginx

echo ""
echo "Installing PDF/Chromium runtime libraries..."
sudo apt-get install -y \
  libnss3 \
  libnspr4 \
  libatk1.0-0 \
  libatk-bridge2.0-0 \
  libcups2 \
  libdrm2 \
  libdbus-1-3 \
  libxkbcommon0 \
  libxcomposite1 \
  libxdamage1 \
  libxfixes3 \
  libxrandr2 \
  libgbm1 \
  libasound2 \
  libpango-1.0-0 \
  libcairo2

echo ""
echo ""
echo "Checking for dotnet..."
if ! command -v dotnet >/dev/null 2>&1; then
  echo "dotnet not found."
  if [[ $NONINTERACTIVE -eq 1 ]]; then
    echo "✖ Non-interactive mode: dotnet SDK must be pre-installed."
    exit 1
  fi

  echo "We can try to install .NET 9 SDK via Microsoft's packages for Debian/Ubuntu."
  confirm_or_exit

  # Detect distro for Microsoft package feed
  DOTNET_CONFIG_URL=""
  if [[ -f /etc/os-release ]]; then
    # shellcheck disable=SC1091
    . /etc/os-release
    case "$ID" in
      debian)
        # Prefer codename if available (bookworm, bullseye, etc.)
        if [[ -n "$VERSION_CODENAME" ]]; then
          DOTNET_CONFIG_URL="https://packages.microsoft.com/config/debian/${VERSION_CODENAME}/packages-microsoft-prod.deb"
        else
          DOTNET_CONFIG_URL="https://packages.microsoft.com/config/debian/${VERSION_ID}/packages-microsoft-prod.deb"
        fi
        ;;
      ubuntu)
        # Use numeric VERSION_ID like 22.04, 24.04, etc.
        DOTNET_CONFIG_URL="https://packages.microsoft.com/config/ubuntu/${VERSION_ID}/packages-microsoft-prod.deb"
        ;;
    esac
  fi

  # Fallback if detection failed
  if [[ -z "$DOTNET_CONFIG_URL" ]]; then
    echo "Could not detect Debian/Ubuntu version from /etc/os-release."
    echo "Falling back to Debian 12 config URL (may or may not work on your distro)."
    DOTNET_CONFIG_URL="https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb"
  fi

  echo "Using Microsoft package config: $DOTNET_CONFIG_URL"
  wget "$DOTNET_CONFIG_URL" -O /tmp/packages-microsoft-prod.deb
  sudo dpkg -i /tmp/packages-microsoft-prod.deb
  rm /tmp/packages-microsoft-prod.deb

  sudo apt-get update -y
  sudo apt-get install -y dotnet-sdk-9.0
else
  echo "dotnet is already installed."
fi

echo ""
echo "Installing dotnet-ef (global tool) if not present..."
if ! dotnet tool list -g | grep -q dotnet-ef; then
  dotnet tool install -g dotnet-ef || true
fi


# ------------------------------
# 4. Create Linux app user
# ------------------------------
if id "$APP_USER" >/dev/null 2>&1; then
  echo ""
  echo "Linux user '$APP_USER' already exists. Reusing it."
else
  echo ""
  echo "Creating Linux user '$APP_USER' to run the Wayfarer service."
  sudo useradd -m -s /bin/bash "$APP_USER"
fi

# ------------------------------
# 5. Create deployment directory
# ------------------------------
echo ""
echo "Creating deployment directory (if needed) and setting ownership."
sudo mkdir -p "$DEPLOY_DIR"
sudo chown -R "$APP_USER":"$APP_USER" "$DEPLOY_DIR"

# ------------------------------
# 6. Configure PostgreSQL
# ------------------------------
echo ""
echo "========================================="
echo " Configuring PostgreSQL"
echo "========================================="

ROLE_EXISTS=$(sudo -u postgres psql -tAc "SELECT 1 FROM pg_roles WHERE rolname='${DB_USER}'" || echo "")
if [[ "$ROLE_EXISTS" == "1" ]]; then
  echo "PostgreSQL role '${DB_USER}' already exists."
else
  echo "Creating PostgreSQL role '${DB_USER}'."
  sudo -u postgres psql -c "CREATE USER ${DB_USER} WITH PASSWORD '${DB_PASS}';"
fi

DB_EXISTS=$(sudo -u postgres psql -tAc "SELECT 1 FROM pg_database WHERE datname='${DB_NAME}'" || echo "")
if [[ "$DB_EXISTS" == "1" ]]; then
  echo "Database '${DB_NAME}' already exists."
else
  echo "Creating database '${DB_NAME}' owned by '${DB_USER}'."
  sudo -u postgres createdb -O "${DB_USER}" "${DB_NAME}"
fi

echo "Enabling PostGIS and citext extensions on '${DB_NAME}'..."
sudo -u postgres psql "${DB_NAME}" -c "CREATE EXTENSION IF NOT EXISTS postgis;"
sudo -u postgres psql "${DB_NAME}" -c "CREATE EXTENSION IF NOT EXISTS citext;"

# ------------------------------
# 7. Systemd service
# ------------------------------
echo ""
echo "========================================="
echo " Installing systemd service"
echo "========================================="

if [[ -f "$SCRIPT_DIR/wayfarer.service" ]]; then
  echo "Installing systemd service file as /etc/systemd/system/${SERVICE_NAME}.service"
  sudo cp "$SCRIPT_DIR/wayfarer.service" "/etc/systemd/system/${SERVICE_NAME}.service"
  sudo systemctl daemon-reload
  sudo systemctl enable "${SERVICE_NAME}" || true
else
  echo "⚠ wayfarer.service not found in $SCRIPT_DIR; skipping systemd setup."
fi

# ------------------------------
# 8. Nginx config (rate-limit + vhost)
# ------------------------------
echo ""
echo "========================================="
echo " Nginx configuration"
echo "========================================="

if [[ -f "$NGINX_RATELIMIT_SRC" ]]; then
  echo "Installing Nginx rate-limit/global config to /etc/nginx/conf.d/nginx-ratelimit.conf"
  sudo cp "$NGINX_RATELIMIT_SRC" /etc/nginx/conf.d/nginx-ratelimit.conf
else
  echo "No nginx-ratelimit.conf found at $NGINX_RATELIMIT_SRC; skipping rate-limit config."
fi

if [[ -f "$NGINX_VHOST_SRC" ]] && grep -q "server\s*{" "$NGINX_VHOST_SRC"; then
  echo "Installing Nginx vhost config to /etc/nginx/sites-available/wayfarer.conf"
  sudo cp "$NGINX_VHOST_SRC" /etc/nginx/sites-available/wayfarer.conf
  if [[ ! -e /etc/nginx/sites-enabled/wayfarer.conf ]]; then
    sudo ln -s /etc/nginx/sites-available/wayfarer.conf /etc/nginx/sites-enabled/wayfarer.conf
  fi
else
  echo "No Nginx vhost file with a 'server { ... }' block found at $NGINX_VHOST_SRC; skipping vhost install."
  echo "You can add it later and reload Nginx."
fi

echo "Testing Nginx configuration..."
sudo nginx -t || true
sudo systemctl reload nginx || true

# ------------------------------
# 8b. Certbot / HTTPS (optional)
# ------------------------------
echo ""
echo "========================================="
echo " HTTPS / Certbot (optional)"
echo "========================================="
echo ""

echo "If your server is reachable from the internet and your DNS points to this machine,"
echo "we can automatically request a Let's Encrypt certificate using Certbot + Nginx plugin."

# Ask user if they want HTTPS via Certbot
if [[ $NONINTERACTIVE -eq 1 ]]; then
  # In non-interactive mode, CERTBOT_ENABLE decides
  :
else
  prompt_yes_no "CERTBOT_ENABLE" "Enable HTTPS via Certbot now? (Y/N)" "N"
fi

if [[ "$CERTBOT_ENABLE" =~ ^[Yy1]$ ]]; then
  # Domain
  if [[ -z "$CERTBOT_DOMAIN" && $NONINTERACTIVE -eq 0 ]]; then
    echo ""
    echo "Enter the primary domain you want to use for Wayfarer (e.g. wayfarer.example.com)."
    read -rp "Domain: " CERTBOT_DOMAIN
  fi

  if [[ -z "$CERTBOT_DOMAIN" ]]; then
    echo "✖ CERTBOT_DOMAIN is required when enabling Certbot."
    exit 1
  fi

  # Email
  if [[ -z "$CERTBOT_EMAIL" && $NONINTERACTIVE -eq 0 ]]; then
    echo ""
    echo "Enter an email address for Let's Encrypt expiry notices and recovery."
    read -rp "Email: " CERTBOT_EMAIL
  fi

  if [[ -z "$CERTBOT_EMAIL" ]]; then
    echo "✖ CERTBOT_EMAIL is required when enabling Certbot."
    exit 1
  fi

  # Staging or production
  if [[ $NONINTERACTIVE -eq 0 ]]; then
    echo ""
    echo "For first tests, it's safer to use the Let's Encrypt STAGING environment (no rate limiting)."
    echo "Use 'Y' for staging while testing, 'N' for real certificates."
    prompt_yes_no "CERTBOT_STAGING" "Use Let's Encrypt staging environment? (Y/N)" "Y"
  fi

  echo ""
  echo "Installing Certbot + Nginx plugin (if not already installed)..."
  sudo apt-get install -y certbot python3-certbot-nginx

  echo ""
  echo "Requesting certificate via Certbot using the Nginx plugin..."
  CERTBOT_ARGS=(
    certonly
    --nginx
    -d "$CERTBOT_DOMAIN"
    -m "$CERTBOT_EMAIL"
    --agree-tos
    --redirect          # auto-add HTTPS redirect in Nginx server block
    --non-interactive
  )

  if [[ "$CERTBOT_STAGING" =~ ^[Yy1]$ ]]; then
    CERTBOT_ARGS+=(--staging)
  fi

  # NOTE: we assume Nginx vhost is already in place and responds on HTTP for $CERTBOT_DOMAIN
  sudo certbot "${CERTBOT_ARGS[@]}"

  echo ""
  echo "Certbot setup complete. Certbot's systemd timer/cron will handle automatic renewal."
else
  echo "Skipping Certbot/HTTPS setup. You can run Certbot later manually, for example:"
  echo "  sudo certbot --nginx -d your.domain.example -m you@example.com --agree-tos --redirect"
fi


# ------------------------------
# 9. Fail2ban config (jails + filters)
# ------------------------------
echo ""
echo "========================================="
echo " Fail2ban configuration (optional)"
echo "========================================="

if command -v fail2ban-server >/dev/null 2>&1; then
  echo "Fail2ban detected. Installing Wayfarer-related jails/filters if present."

  # Jail config (your uploaded wayfarer-nginx.conf is a jail file)
  if [[ -f "$SCRIPT_DIR/wayfarer-nginx.conf" ]]; then
    sudo cp "$SCRIPT_DIR/wayfarer-nginx.conf" /etc/fail2ban/jail.d/wayfarer-nginx.conf
  fi

  # Filters
  if [[ -f "$SCRIPT_DIR/wayfarer-nginx-404.conf" ]]; then
    sudo cp "$SCRIPT_DIR/wayfarer-nginx-404.conf" /etc/fail2ban/filter.d/wayfarer-nginx-404.conf
  fi
  if [[ -f "$SCRIPT_DIR/wayfarer-nginx-login.conf" ]]; then
    sudo cp "$SCRIPT_DIR/wayfarer-nginx-login.conf" /etc/fail2ban/filter.d/wayfarer-nginx-login.conf
  fi
  if [[ -f "$SCRIPT_DIR/wayfarer-nginx-scanner.conf" ]]; then
    sudo cp "$SCRIPT_DIR/wayfarer-nginx-scanner.conf" /etc/fail2ban/filter.d/wayfarer-nginx-scanner.conf
  fi

  sudo systemctl restart fail2ban || true
else
  echo "Fail2ban not installed; skipping Fail2ban integration."
fi

# ------------------------------
# 10. Connection string helper
# ------------------------------
CONN_STR="Host=localhost;Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASS}"

echo ""
echo "========================================="
echo " Connection string for appsettings.Production.json"
echo "========================================="
echo ""
echo "Suggested connection string:"
echo ""
echo "  \"ConnectionStrings\": {"
echo "    \"DefaultConnection\": \"${CONN_STR}\""
echo "  }"
echo ""
echo "Place this into:"
echo "  ${APP_DIR}/appsettings.Production.json"
echo ""

if [[ $NONINTERACTIVE -eq 0 ]]; then
  read -rp "Open appsettings.Production.json in \$EDITOR now? [y/N]: " EDIT_CFG
  EDIT_CFG="${EDIT_CFG:-N}"
  if [[ "$EDIT_CFG" =~ ^[Yy]$ ]]; then
    EDITOR_CMD="${EDITOR:-nano}"
    "$EDITOR_CMD" "${APP_DIR}/appsettings.Production.json"
  fi
fi

# ------------------------------
# 11. Initial deployment via deploy.sh
# ------------------------------
echo ""
echo "========================================="
echo " Initial deployment"
echo "========================================="
echo ""
echo "We are ready to run deploy.sh for the first time."

if [[ $NONINTERACTIVE -eq 1 ]]; then
  RUN_DEPLOY="Y"
else
  read -rp "Run deploy.sh now? [Y/n]: " RUN_DEPLOY
  RUN_DEPLOY="${RUN_DEPLOY:-Y}"
fi

if [[ "$RUN_DEPLOY" =~ ^[Yy]$ ]]; then
  if [[ ! -x "$SCRIPT_DIR/deploy.sh" ]]; then
    chmod +x "$SCRIPT_DIR/deploy.sh"
  fi

  echo "Running deploy.sh..."
  APP_DIR="$APP_DIR" \
  DEPLOY_DIR="$DEPLOY_DIR" \
  APP_USER="$APP_USER" \
  SERVICE_NAME="$SERVICE_NAME" \
  DOTNET_ENVIRONMENT="$DOTNET_ENVIRONMENT" \
  REF="$REF" \
  "$SCRIPT_DIR/deploy.sh"
else
  echo "Skipping deploy.sh. You can later run:"
  echo "  APP_DIR=\"$APP_DIR\" DEPLOY_DIR=\"$DEPLOY_DIR\" APP_USER=\"$APP_USER\" SERVICE_NAME=\"$SERVICE_NAME\" REF=\"$REF\" ./deploy.sh"
fi

# ------------------------------
# 12. Final info
# ------------------------------
echo ""
echo "========================================="
echo " Installation Complete"
echo "========================================="
echo ""
echo "Check service status:"
echo "  sudo systemctl status ${SERVICE_NAME}"
echo ""
echo "Tail logs:"
echo "  sudo journalctl -u ${SERVICE_NAME} -f"
echo ""
echo "Default admin login (if not changed in docs):"
echo "  Username: admin"
echo "  Password: Admin1!"
echo ""
echo "If you ever want to remove everything that install.sh created,"
echo "you can run ./uninstall.sh."
