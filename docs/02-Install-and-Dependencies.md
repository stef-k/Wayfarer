# Install & Self-Hosting

## Overview

Wayfarer is designed for self-hosting by power users, small businesses, and organizations on their own infrastructure. Whether you're running on a Raspberry Pi, home server, VPS, or dedicated server, Wayfarer gives you full control over your location data and trip planning.

---

## Quick Start

### Core Dependencies

- **.NET 9 SDK** - Application runtime
- **PostgreSQL 13+** with **PostGIS extension** - Database
- **Nginx** (or similar reverse proxy) - Recommended for production

### Optional: PDF Export Feature

If you want to export trips as PDF documents, you'll need:

- **Chrome system libraries** (Linux only)
- Chrome browser is **automatically downloaded** by the application on first PDF export
- No manual Chrome installation needed - it's handled automatically!

**Linux users:** See [Install Chromium Runtime Dependencies (PDF export)](26-Deployment.md#6-install-chromium-runtime-dependencies-pdf-export)

**Windows users:** No additional setup needed - Chrome downloads automatically.

> **Note:** If PDF export doesn't work after installation, see the [PDF Export / Playwright Issues](26-Deployment.md#pdf-export--playwright-issues) guide.

### Basic Setup Steps

1. **Install dependencies** (.NET 9, PostgreSQL + PostGIS, Nginx)
2. **Create database and user** with PostGIS enabled
3. **Clone or download** Wayfarer
4. **Configure connection string** in `appsettings.json`
5. **Run the application** - Database tables and initial data are created automatically
6. **Login as admin** and change the default password

### Default Admin Credentials

On first run, Wayfarer automatically creates:

- **Username:** `admin`
- **Password:** `Admin1!`

**⚠️ Change this password immediately after first login!**

---

## What Gets Initialized Automatically

When you first run Wayfarer, it automatically:

- ✅ Creates all database tables via migrations
- ✅ Seeds system roles (Admin, Manager, User)
- ✅ Creates the default admin user
- ✅ Initializes application settings with defaults
- ✅ Seeds 69 predefined activity types for location categorization

**No manual database setup or SQL scripts needed!**

---

## Platform-Specific Quick Start

### Linux (Ubuntu/Debian)

```bash
# Install core dependencies
# 1) Add Microsoft package repo for .NET (required on a fresh system)
wget https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb
sudo dpkg -i packages-microsoft-prod.deb && rm packages-microsoft-prod.deb
sudo apt update
# 2) Install packages
sudo apt install -y dotnet-sdk-9.0 postgresql postgis nginx

# Install Chrome dependencies (for PDF export)
sudo apt install -y libnss3 libgbm1 libasound2 libatk-bridge2.0-0 \
    libcups2 libdrm2 libpango-1.0-0 libcairo2

# Create database
# (Optional) create a dedicated DB user, matching install.sh defaults
sudo -u postgres psql -c "CREATE USER wayfarer_user WITH PASSWORD 'your-strong-password';"
sudo -u postgres createdb wayfarer
sudo -u postgres psql wayfarer -c "CREATE EXTENSION postgis;"
sudo -u postgres psql wayfarer -c "CREATE EXTENSION citext;"

If you use deployment/install.sh, it will prompt for DB name/user/password and create the database and extensions automatically (defaults: wayfarer / wayfarer_user).

# Clone and configure
git clone https://github.com/yourusername/wayfarer.git
cd wayfarer
nano appsettings.json  # Set your connection string

# Run
dotnet run
```

### Windows

1. Install .NET 9 SDK from [microsoft.com/dotnet](https://dotnet.microsoft.com/download)
2. Install PostgreSQL + PostGIS from [postgresql.org (Windows installer)](https://www.postgresql.org/download/windows/) or [enterprisedb.com](https://www.enterprisedb.com/downloads/postgres-postgresql-downloads)
3. Clone the repository
4. Configure `appsettings.Development.json` with your connection string
5. Run `dotnet restore` then `dotnet run`
6. Visit `http://localhost:5000`

---

## Production Deployment

For detailed production deployment instructions including:

- System user setup and security
- Nginx reverse proxy configuration with SSE/WebSocket support
- HTTPS setup with Let's Encrypt/Certbot
- Systemd service configuration
- Directory permissions and structure
- Log rotation and monitoring
- Update procedures and troubleshooting

**→ See the comprehensive [Deployment Guide](26-Deployment.md)**

---

## Configuration

### Connection String

Edit `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=localhost;Database=wayfarer;Username=youruser;Password=yourpassword"
  }
}
```

### Important Settings

- **`Logging:LogFilePath:Default`** - Where application logs are written (ensure directory exists and is writable)
- **`CacheSettings:TileCacheDirectory`** - Where map tiles are cached locally
- **`AllowedHosts`** - Configure for your domain in production

### Environment-Specific Configuration

- **Development:** Use `appsettings.Development.json`
- **Production:** Use `appsettings.Production.json`
- Set `ASPNETCORE_ENVIRONMENT` environment variable to switch between environments

---

## Admin Setup

After installation:

1. **Sign in as admin** using the seeded credentials (change password immediately!)
2. **Review Application Settings:**
   - Location tracking thresholds
   - Upload size limits
   - Tile cache size
   - Registration open/closed
3. **Configure security:**
   - Set up HTTPS (use Let's Encrypt for free SSL certificates)
   - Enable two-factor authentication from your account settings
   - Rotate API tokens regularly
4. **Create users:**
   - Open registration for self-service
   - Or manually create user accounts through the admin panel

---

## Security Essentials

- **HTTPS Required:** Always run behind HTTPS in production (use Let's Encrypt/Certbot)
- **Change Default Credentials:** Immediately change the admin password
- **Keep API Tokens Secret:** Treat API tokens like passwords
- **Regular Updates:** Pull latest code and apply updates regularly
- **Backup Database:** Schedule regular PostgreSQL backups
- **Firewall Configuration:** Only expose ports 80, 443, and SSH
- You can let `deployment/install.sh` install Certbot and request a Let’s Encrypt certificate automatically (recommended for first-time setup), or run Certbot manually later.

---

## Getting Help

- **Detailed Installation:** [Developer Deployment Guide](26-Deployment.md)
- **Configuration Reference:** [Developer Configuration Guide](16-Configuration.md)
- **Troubleshooting:** [User Troubleshooting Guide](10-Troubleshooting.md)
- **GitHub Issues:** Report bugs and request features

---

**Ready to deploy?** Check out the [full deployment guide](26-Deployment.md) for step-by-step instructions!
