# Jobs (Quartz)

Scheduler
- Quartz configured with ADO job store; see `ConfigureQuartz` in `Program.cs`.
- Jobs run in DI scopes via `ScopedJobFactory`. A `JobExecutionListener` logs lifecycle events.

Built‑In Jobs
- `LocationImportJob` — processes queued imports in batches.
- `LogCleanupJob` — prunes logs/files based on retention policy.
- `AuditLogCleanupJob` — trims audit table by retention settings.

Persistence
- `qrtz_*` tables created automatically at startup if missing.

Admin UI
- `Areas/Admin/Controllers/JobsController` manages and monitors scheduled jobs and history.

