#!/bin/bash
# ============================================================================
# Wayfarer Deployment Script
# ============================================================================
#
# This script automates the deployment process for Wayfarer:
# - Pulls latest code from Git
# - Builds the application
# - Applies database migrations
# - Deploys to production directory
# - Restarts the service
#
# CUSTOMIZE FOR YOUR DEPLOYMENT:
# - Update the paths below to match your server setup
# - Ensure your system user has sudo privileges for systemctl
#
# USAGE:
#   # Deploy from main branch:
#   ./deploy.sh
#
#   # Deploy from a specific branch:
#   REF=develop ./deploy.sh
#
#   # Deploy from a tag:
#   REF=v1.0.0 ./deploy.sh
#
# RECOMMENDED:
# - Run this script as the user that owns the repo (e.g. "wayfarer" or your login user)
# - That user must have sudo privileges for systemctl/rsync/chown
#
# DO NOT run this as root unless you know what you're doing.
# ============================================================================

set -e  # Exit on any error

# ============================================================================
# CONFIGURATION - CUSTOMIZE THESE FOR YOUR DEPLOYMENT
# ============================================================================

# Directory where you cloned the Wayfarer repository
# IMPORTANT:
# - Change /home/youruser/Wayfarer to the path where you cloned the repo
# - Or pass APP_DIR=/path/to/Wayfarer when running the script
APP_DIR="${APP_DIR:-/home/youruser/Wayfarer}"

# Temporary output directory for dotnet publish
# This gets created fresh on each deployment
OUT_DIR="$APP_DIR/out"

# Production deployment directory where the app runs from
# This is the directory referenced in your systemd service
DEPLOY_DIR="${DEPLOY_DIR:-/var/www/wayfarer}"

# Systemd service name
# Check with: systemctl list-units | grep wayfarer
# Create a systemd service (e.g. /etc/systemd/system/wayfarer.service) that:
# - Uses User=wayfarer
# - Uses WorkingDirectory=/var/www/wayfarer
# - Runs dotnet Wayfarer.dll
SERVICE_NAME="${SERVICE_NAME:-wayfarer}"

# System user that runs the application
# This user should own the deployment directory
APP_USER="${APP_USER:-wayfarer}"

# Dotnet environment for migrations
DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Production}"

# Git branch/tag to deploy (can be overridden with REF env variable)
REF="${REF:-main}"

# ============================================================================
# DEPLOYMENT PROCESS
# ============================================================================

echo "========================================="
echo "Wayfarer Deployment Script"
echo "========================================="
echo "Repository:    $APP_DIR"
echo "Deploy to:     $DEPLOY_DIR"
echo "Service:       $SERVICE_NAME"
echo "App user:      $APP_USER"
echo "Git ref:       $REF"
echo "========================================="
echo ""

# Step 1: Navigate to repository
echo "[1/8] Changing to repository: $APP_DIR"
cd "$APP_DIR"

# Step 2: Fetch latest changes
echo "[2/8] Fetching latest commits and tags..."
git fetch --prune --tags origin

# Step 3: Checkout requested branch or tag
if git show-ref --quiet "refs/remotes/origin/$REF"; then
  echo "[3/8] Checking out branch: $REF"
  git checkout -B "$REF" "origin/$REF"
elif git show-ref --quiet "refs/tags/$REF"; then
  echo "[3/8] Checking out tag (detached): $REF"
  git checkout --detach "$REF"
else
  echo "✖ Error: Ref '$REF' not found as origin branch or tag" >&2
  exit 1
fi

echo ""
echo "Current HEAD:"
git show --quiet --pretty='format:%h %ci %s'
echo ""
echo ""

# Step 4: Restore dependencies and tools
echo "[4/8] Restoring dependencies and tools..."
dotnet restore Wayfarer.csproj
dotnet tool restore

# Step 4.5: Clean and prepare
echo "[4.5/8] Clearing output directory: $OUT_DIR"
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

echo "[4.6/8] Cleaning build artifacts..."
dotnet clean Wayfarer.csproj -c Release
rm -rf "$APP_DIR/wwwroot/dist"
rm -f "$APP_DIR/wwwroot/frontend.manifest.json"

# Step 5: Build and publish
echo "[5/8] Building project to $OUT_DIR..."
export DOTNET_ENVIRONMENT
dotnet build Wayfarer.csproj -c Release --no-restore
dotnet publish Wayfarer.csproj -c Release -o "$OUT_DIR" --no-build

# Step 5: Apply database migrations
# Requires dotnet-ef installed as a global tool:
#   dotnet tool install -g dotnet-ef
echo "[6/8] Applying EF Core migrations..."
export PATH="$PATH:$HOME/.dotnet/tools"

# Read connection string from systemd service file (secrets are stored there, not in appsettings.json)
SERVICE_FILE="/etc/systemd/system/${SERVICE_NAME}.service"
if [ -f "$SERVICE_FILE" ]; then
    # Extract the connection string from Environment="ConnectionStrings__DefaultConnection=..."
    CONN_LINE=$(sudo grep 'ConnectionStrings__DefaultConnection' "$SERVICE_FILE" 2>/dev/null || true)
    if [ -n "$CONN_LINE" ]; then
        # Parse: Environment="ConnectionStrings__DefaultConnection=value" → export the variable
        # Handle both quoted and unquoted formats
        CONN_VALUE=$(echo "$CONN_LINE" | sed -n 's/^[[:space:]]*Environment="\?\(ConnectionStrings__DefaultConnection=[^"]*\)"\?$/\1/p')
        if [ -n "$CONN_VALUE" ]; then
            export "$CONN_VALUE"
            echo "  → Using connection string from $SERVICE_FILE"
        fi
    fi
fi

# Verify connection string is available
if [ -z "$ConnectionStrings__DefaultConnection" ]; then
    echo "  ⚠ Warning: ConnectionStrings__DefaultConnection not found in $SERVICE_FILE"
    echo "    Migrations will use appsettings.json (may have placeholder password)"
    echo "    To fix: ensure your systemd service file has:"
    echo "      Environment=\"ConnectionStrings__DefaultConnection=Host=...;Password=...\""
fi

DOTNET_ENVIRONMENT=$DOTNET_ENVIRONMENT dotnet ef database update --project Wayfarer.csproj

# Step 5.5: Pre-install Playwright browsers (optional, recommended)
# Uncomment the following lines to pre-install Chromium (~400MB download)
# This avoids slow first PDF export in production
# echo "[6.5/8] Installing Playwright browsers..."
# pwsh "$OUT_DIR/playwright.ps1" install chromium

# Step 6: Stop service
echo "[7/8] Stopping $SERVICE_NAME service..."
sudo systemctl stop "$SERVICE_NAME"

# Step 7: Deploy files
echo "Deploying to $DEPLOY_DIR (excluding Uploads, TileCache, ChromeCache, Logs, wwwroot/thumbs)..."
sudo rsync -av --delete \
  --exclude 'Uploads' \
  --exclude 'TileCache' \
  --exclude 'ChromeCache' \
  --exclude 'Logs' \
  --exclude 'wwwroot/thumbs/' \
  --exclude 'tests' \
  --exclude 'coverage' \
  --exclude 'coverage-report' \
  --exclude 'coverlet.runsettings' \
  "$OUT_DIR"/ "$DEPLOY_DIR"/

# Step 8: Fix permissions and restart
echo "Fixing ownership to $APP_USER for $DEPLOY_DIR..."
sudo chown -R "$APP_USER":"$APP_USER" "$DEPLOY_DIR"

# Ensure writable directories exist and have correct permissions
echo "Ensuring writable directories exist..."
sudo mkdir -p "$DEPLOY_DIR/Uploads" "$DEPLOY_DIR/TileCache" "$DEPLOY_DIR/ChromeCache" "$DEPLOY_DIR/Logs"
sudo chown -R "$APP_USER":"$APP_USER" "$DEPLOY_DIR/Uploads" "$DEPLOY_DIR/TileCache" "$DEPLOY_DIR/ChromeCache" "$DEPLOY_DIR/Logs"
sudo chmod 755 "$DEPLOY_DIR/Uploads" "$DEPLOY_DIR/TileCache" "$DEPLOY_DIR/ChromeCache" "$DEPLOY_DIR/Logs"

echo "[8/8] Starting $SERVICE_NAME service..."
sudo systemctl start "$SERVICE_NAME"

echo ""
echo "========================================="
echo "✔ Deployment complete for ref: $REF"
echo "========================================="
echo ""
echo "Next steps:"
echo "  - Check service status: sudo systemctl status $SERVICE_NAME"
echo "  - View logs: sudo journalctl -u $SERVICE_NAME -f"
echo ""
