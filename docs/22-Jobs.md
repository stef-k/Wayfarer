# Jobs (Quartz)

Wayfarer uses Quartz.NET for background job scheduling and execution.

---

## Scheduler Configuration

- Quartz configured with **ADO.NET job store** for persistence; see `ConfigureQuartz` in `Program.cs`.
- Jobs run in DI scopes via `ScopedJobFactory`.
- `JobExecutionListener` logs lifecycle events and records execution history.
- `qrtz_*` tables created automatically at startup if missing.
- Job type name migrations handled by `QuartzSchemaInstaller`.

---

## Built-In Jobs

### LocationImportJob
- **Purpose**: Processes queued location imports in batches.
- **Trigger**: Scheduled when user uploads a file.
- **Features**:
  - Progress tracking via SSE.
  - Supports cancellation via `CancellationToken`.
  - Processes JSON (Google Timeline), GPX, and KML files.
- **Key File**: `Jobs/LocationImportJob.cs`

### VisitCleanupJob
- **Purpose**: Cleans up stale visit data globally.
- **Schedule**: Runs periodically (configurable).
- **Actions**:
  - Closes open visits with no pings beyond the configured threshold.
  - Deletes stale visit candidates that were never confirmed.
- **Settings Used**:
  - `VisitedEndVisitAfterMinutes` (derived from `LocationTimeThresholdMinutes × 9`)
  - `VisitedCandidateStaleMinutes` (derived from `LocationTimeThresholdMinutes × 12`)
- **Key File**: `Jobs/VisitCleanupJob.cs`

### AuditLogCleanupJob
- **Purpose**: Removes audit log entries older than 2 years.
- **Schedule**: Runs periodically.
- **Supports**: Cancellation via `CancellationToken`.
- **Key File**: `Jobs/AuditLogCleanupJob.cs`

### LogCleanupJob
- **Purpose**: Prunes application log files older than 1 month.
- **Schedule**: Runs periodically.
- **Supports**: Cancellation via `CancellationToken`.
- **Key File**: `Jobs/LogCleanupJob.cs`

---

## Job Status Tracking

All jobs update their status in `JobDataMap`:
- `Scheduled` — job is queued.
- `In Progress` — job is currently executing.
- `Completed` — job finished successfully.
- `Cancelled` — job was cancelled via cancellation token.
- `Failed` — job encountered an error.

Status messages provide additional details (e.g., "Deleted 5 old log files").

---

## Job Execution History

- `JobExecutionListener` records each job execution to the `JobHistories` table.
- Tracks: job name, status, last run time, error messages.
- Viewable in Admin > Jobs panel.

---

## Admin Job Control Panel

Located at **Admin > Jobs**, the control panel provides:

### Monitoring
- View all scheduled jobs with next fire time.
- See current status (Running, Paused, Scheduled).
- View last run time and status message.

### Controls
- **Pause** — temporarily stop a job's triggers.
- **Resume** — restart paused job triggers.
- **Cancel** — request cancellation of a running job (jobs must respect `CancellationToken`).
- **Trigger Now** — manually fire a job immediately.

### Real-Time Updates
- SSE stream (`/api/sse/stream/job-status`) pushes status changes.
- UI updates automatically without page refresh.

---

## Job Persistence

Quartz tables (`qrtz_*`) store:
- Job definitions and triggers.
- Cron expressions and schedules.
- Job data maps with status information.

Tables are created automatically on first startup if missing.

---

## Adding Custom Jobs

1. Create a class implementing `IJob`.
2. Inject dependencies via constructor (jobs run in DI scope).
3. Use `context.CancellationToken` for cancellation support.
4. Update `context.JobDetail.JobDataMap["Status"]` for monitoring.
5. Register in `Program.cs` within `ConfigureQuartz`.

Example:
```csharp
public class MyCustomJob : IJob
{
    public async Task Execute(IJobExecutionContext context)
    {
        var ct = context.CancellationToken;
        var jobDataMap = context.JobDetail.JobDataMap;

        jobDataMap["Status"] = "In Progress";

        try
        {
            ct.ThrowIfCancellationRequested();
            // Do work...
            jobDataMap["Status"] = "Completed";
        }
        catch (OperationCanceledException)
        {
            jobDataMap["Status"] = "Cancelled";
        }
    }
}
```
