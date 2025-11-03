# Setup

Prerequisites
- .NET 9 SDK
- PostgreSQL with PostGIS extension
- Windows 10 (primary dev OS) or Linux/macOS for development

Restore, Build, Run
- Restore: `dotnet restore`
- Build: `dotnet build`
- Run (Development): `dotnet run` (loads `appsettings.Development.json`)
- Hot‑reload: `dotnet watch run`

Database
- Configure `ConnectionStrings:DefaultConnection` for your local PostgreSQL.
- Ensure PostGIS is enabled for the target database.
- The app auto‑creates Quartz tables at startup (`QuartzSchemaInstaller`).

Admin CLI
- Reset password: `dotnet run -- reset-password <username> <new-password>`
- Use temporary values; rotate immediately. Do not document real passwords.

Mobile App (Separate Repo)
- Location: `Wayfarer.Mobile`.
- Configure the mobile app to your server URL; no central domain.

