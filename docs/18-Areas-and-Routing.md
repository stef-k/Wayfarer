# Areas & Routing

Areas
- `Areas/Admin` — Admin dashboards: users, roles, settings, jobs, audit logs, activity types, API tokens.
- `Areas/Api` — JSON APIs for trips, locations, groups, invitations, SSE, icons, settings.
- `Areas/Identity` — Identity UI for login/registration and account management.
- `Areas/Manager` — Manager dashboards for groups, users, API tokens (delegated scope).
- `Areas/User` — User dashboards and views.
- `Areas/Public` — Public views (where applicable), plus general `Views/` for non‑area pages.

Routing
- MVC controllers under `Controllers/` and per‑area controllers follow conventional routes.
- API controllers use `[Area("Api")]` and `[Route("api/[controller]")]`.
- Trip exports: `TripExportController` uses routes like `Trip/ExportPdf/{id}`.

Public Timeline
- User public timeline: `GET /Public/Users/Timeline/{username}` (full page view)
- Embeddable view: `GET /Public/Users/Timeline/{username}/embed`
- Data API: `POST /Public/Users/GetPublicTimeline` (with viewport/zoom filter), `GET /Public/Users/GetPublicStats/{username}`
- Hidden Areas are enforced: locations inside user-defined hidden polygons are excluded from public results.

Base Controllers
- `Controllers/BaseController` — shared helpers for MVC controllers (logging, alerts, titles).
- `Areas/Api/Controllers/BaseApiController` — token extraction, role helpers, numeric/geometry sanitizers.
