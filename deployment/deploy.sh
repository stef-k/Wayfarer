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
#   # Deploy from master branch:
#   ./deploy.sh
#
#   # Deploy from a specific branch:
#   REF=develop ./deploy.sh
#
#   # Deploy from a tag:
#   REF=v1.0.0 ./deploy.sh
#
# ============================================================================

set -e  # Exit on any error

# ============================================================================
# CONFIGURATION - CUSTOMIZE THESE FOR YOUR DEPLOYMENT
# ============================================================================

# Directory where you cloned the Wayfarer repository
# This is where you run git pull and dotnet build
APP_DIR="${APP_DIR:-/home/youruser/Wayfarer}"

# Temporary output directory for dotnet publish
# This gets created fresh on each deployment
OUT_DIR="$APP_DIR/out"

# Production deployment directory where the app runs from
# This is the directory referenced in your systemd service
DEPLOY_DIR="${DEPLOY_DIR:-/var/www/wayfarer}"

# Systemd service name
# Check with: systemctl list-units | grep wayfarer
SERVICE_NAME="${SERVICE_NAME:-wayfarer}"

# System user that runs the application
# This user should own the deployment directory
APP_USER="${APP_USER:-wayfarer}"

# Dotnet environment for migrations
DOTNET_ENVIRONMENT="${DOTNET_ENVIRONMENT:-Production}"

# Git branch/tag to deploy (can be overridden with REF env variable)
REF="${REF:-master}"

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

# Step 4: Build application
echo "[4/8] Clearing output directory: $OUT_DIR"
rm -rf "$OUT_DIR"
mkdir -p "$OUT_DIR"

echo "[5/8] Building project to $OUT_DIR..."
export DOTNET_ENVIRONMENT
dotnet publish -c Release -o "$OUT_DIR"

# Step 5: Apply database migrations
echo "[6/8] Applying EF Core migrations..."
export PATH="$PATH:$HOME/.dotnet/tools"
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
echo "Deploying to $DEPLOY_DIR (excluding Uploads, TileCache, ChromeCache)..."
sudo rsync -av --delete \
  --exclude 'Uploads' \
  --exclude 'TileCache' \
  --exclude 'ChromeCache' \
  --exclude 'Logs' \
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
