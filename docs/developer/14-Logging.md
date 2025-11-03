# Logging & Auditing

Serilog
- Configured in `Program.cs` using console and rolling file sinks.
- PostgreSQL sink writes to table `AuditLogs` (auto‑created if absent).

Config
- `Logging:LogFilePath:Default` — ensure directory exists and writable by the app.
- `Logging:LogLevel:*` — tune verbosity. Development uses more verbose levels.

Middleware
- `PerformanceMonitoringMiddleware` logs request timings.
- `DynamicRequestSizeMiddleware` sets max request body size from runtime settings.

Audit
- User and admin actions are logged in database and file. Do not log secrets or passwords.

Retention
- Cleanup jobs trim older logs; adjust retention policy per deployment.

