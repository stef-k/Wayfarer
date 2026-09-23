# Setup

Prerequisites
- .NET 10 SDK
- PostgreSQL 17 with PostGIS extension
- Node.js 24.x/npm for Trip Editor Vite builds
- Ubuntu 24.04 on Linux/WSL2 is the primary maintainer baseline; Windows remains an alternative.

## Linux/WSL Development

- Clone into the Linux filesystem (for example, `~/src/Wayfarer`), not `/mnt/c`, and run .NET, npm, and PostgreSQL tools from the Linux shell.
- With nvm installed, run `nvm install` then `nvm use` in the checkout; `.nvmrc` selects Node 24 LTS. Run `npm ci` to restore the locked frontend dependencies.
- Keep credentials in .NET user-secrets or ignored local configuration. Configure writable local cache/log paths for your environment.
- Run `npm run dev` alongside `dotnet run` for the Trip Editor; validate with `npm run typecheck`, `npm run test:client`, and `npm run build`.
- Attach the dedicated PostgreSQL test connection as described in [Testing](22-Testing.md#persistent-postgresql-test-database). Do not run relational fixtures against your useful development data.

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
- Help: `dotnet run --no-launch-profile -- help`
- Version: `dotnet run --no-launch-profile -- version`
- Reset password: `dotnet run -- reset-password <username> <new-password>`
- Use temporary values; rotate immediately. Do not document real passwords.

Frontend Development
- **Bundling**: [MvcFrontendKit](https://github.com/nickofc/MvcFrontendKit) for JavaScript/CSS bundling.
  - Configuration: `frontend.config.yaml`
  - Convention: JS files in `wwwroot/js/Areas/{Area}/{Controller}/{Action}.js` auto-link to matching views.
  - Development: runs unbundled for debugging.
  - Production prerequisite: run `dotnet tool restore`, then `dotnet frontend build` to generate minified bundles in `/dist` with the repository-local CLI.
- **Trip Editor**: Vue/Vite builds static assets under `wwwroot/vite/trip-editor`.
  - Production prerequisite: run `npm ci` and `npm run build` before publishing when building on the server.
  - Production output must include `wwwroot/vite/trip-editor/manifest.json` and the CSS/JS files referenced by that manifest.
  - Generated Vite output is not committed. Production still runs as one ASP.NET Core app with no Node runtime service or SSR server.
- **State Management**: Trip editing state is owned by the Vue Trip Editor under `ClientApps/trip-editor/src`.
  - Key files: `ClientApps/trip-editor/src/App.vue`, `ClientApps/trip-editor/src/api/tripEditorApi.ts`
- **Map Icons**: [wayfarer-map-icons](https://github.com/stef-k/wayfarer-map-icons) provides consistent marker icons.
  - Location: `wwwroot/icons/wayfarer-map-icons/`
- Global assets: `site.js` and `site.css` load on every page.

Production-like Bundle Acceptance
- Build frontend prerequisites first: `dotnet tool restore`, `dotnet frontend build`, then `npm ci` and `npm run build`.
- Publish and run the published output:

```bash
dotnet publish Wayfarer.csproj -c Release -o ./out
cd ./out
ASPNETCORE_ENVIRONMENT=Production dotnet Wayfarer.dll --urls=http://localhost:5000
```

- Do not use source-tree `ASPNETCORE_ENVIRONMENT=Production dotnet run` as a bundle acceptance path. Razor scoped CSS such as `Wayfarer.styles.css` is produced as a static web asset outside `wwwroot` during local builds, and published output is the supported production-like asset layout.
- Confirm the published output contains `wwwroot/vite/trip-editor/manifest.json` plus the referenced Trip Editor CSS/JS files before treating the bundle acceptance as complete.

Mobile App (Separate Repo)
- Location: `WayfarerMobile`.
- Configure the mobile app to your server URL; no central domain.

