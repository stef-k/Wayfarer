# Setup

Prerequisites
- .NET 10 SDK
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

Frontend Development
- **Bundling**: [MvcFrontendKit](https://github.com/nickofc/MvcFrontendKit) for JavaScript/CSS bundling.
  - Configuration: `frontend.config.yaml`
  - Convention: JS files in `wwwroot/js/Areas/{Area}/{Controller}/{Action}.js` auto-link to matching views.
  - Development: runs unbundled for debugging.
  - Production: run `dotnet mvcfrontendkit build` to generate minified bundles in `/dist`.
- **State Management**: Trip editing uses reactive store pattern inspired by [simple-reactive-store](https://github.com/stef-k/simple-reactive-store).
  - Key files: `wwwroot/js/Areas/User/Trip/store.js`, `storeInstance.js`
- **Map Icons**: [wayfarer-map-icons](https://github.com/stef-k/wayfarer-map-icons) provides consistent marker icons.
  - Location: `wwwroot/icons/wayfarer-map-icons/`
- Global assets: `site.js` and `site.css` load on every page.

Mobile App (Separate Repo)
- Location: `WayfarerMobile`.
- Configure the mobile app to your server URL; no central domain.

