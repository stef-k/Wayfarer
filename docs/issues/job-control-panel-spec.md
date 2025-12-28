## Goal

Enhance the Admin Jobs panel to provide full job lifecycle control: pause scheduled execution, resume paused jobs, and cancel running jobs.

---

## Current State

| Capability | Available? |
|------------|------------|
| View job status | ✅ |
| View next/last run times | ✅ |
| Manual trigger (Start) | ✅ |
| Pause scheduled execution | ❌ |
| Resume paused job | ❌ |
| Cancel running job | ❌ |
| See if job is currently running | ❌ |
| See if job is paused | ❌ |

---

## Quartz.NET API Reference

Based on [Quartz.NET IScheduler interface](https://github.com/quartznet/quartznet/blob/main/src/Quartz/IScheduler.cs):

### Pause/Resume Methods
```csharp
// Pause a job's triggers - job won't fire while paused
ValueTask PauseJob(JobKey jobKey, CancellationToken cancellationToken = default);

// Resume a paused job
ValueTask ResumeJob(JobKey jobKey, CancellationToken cancellationToken = default);
```

### Interrupt Methods
```csharp
// Request interruption of a running job (returns true if job was found and interrupted)
ValueTask<bool> Interrupt(JobKey jobKey, CancellationToken cancellationToken = default);
```

### State Checking Methods
```csharp
// Get list of currently executing jobs
ValueTask<List<IJobExecutionContext>> GetCurrentlyExecutingJobs(CancellationToken cancellationToken = default);

// Get trigger state (Normal, Paused, Complete, Error, Blocked, None)
ValueTask<TriggerState> GetTriggerState(TriggerKey triggerKey, CancellationToken cancellationToken = default);
```

---

## Implementation Plan

### Phase 1: Update ViewModel

**File:** `Models/ViewModels/JobMonitoringViewModel.cs`

Add new properties:
```csharp
public class JobMonitoringViewModel
{
    // Existing properties...
    public string JobName { get; set; } = string.Empty;
    public string JobGroup { get; set; } = string.Empty;
    public DateTimeOffset? NextFireTime { get; set; }
    public DateTimeOffset? LastRunTime { get; set; }
    public string Status { get; set; } = string.Empty;

    // New properties
    public bool IsRunning { get; set; }        // Currently executing
    public bool IsPaused { get; set; }         // Triggers are paused
    public bool IsInterruptable { get; set; } // Job implements IInterruptableJob or uses CancellationToken
}
```

### Phase 2: Update JobsController

**File:** `Areas/Admin/Controllers/JobsController.cs`

#### 2.1 Enhance Index Action
```csharp
public async Task<IActionResult> Index()
{
    var jobKeys = await _scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
    var currentlyExecuting = await _scheduler.GetCurrentlyExecutingJobs();
    var runningJobKeys = currentlyExecuting.Select(c => c.JobDetail.Key).ToHashSet();

    var jobs = new List<JobMonitoringViewModel>();

    foreach (var jobKey in jobKeys)
    {
        var jobDetail = await _scheduler.GetJobDetail(jobKey);
        var triggers = await _scheduler.GetTriggersOfJob(jobKey);
        var trigger = triggers.FirstOrDefault();

        // Check if paused by examining trigger state
        var isPaused = trigger != null &&
            await _scheduler.GetTriggerState(trigger.Key) == TriggerState.Paused;

        // Check if currently running
        var isRunning = runningJobKeys.Contains(jobKey);

        // Check if interruptable (job uses CancellationToken pattern)
        var isInterruptable = true; // All our jobs should support this

        jobs.Add(new JobMonitoringViewModel
        {
            JobName = jobKey.Name,
            JobGroup = jobKey.Group,
            NextFireTime = trigger?.GetNextFireTimeUtc()?.ToLocalTime(),
            LastRunTime = await GetLastRunTime(jobKey.Name),
            Status = DetermineStatus(isRunning, isPaused, jobDetail),
            IsRunning = isRunning,
            IsPaused = isPaused,
            IsInterruptable = isInterruptable
        });
    }

    return View("Index", jobs);
}

private string DetermineStatus(bool isRunning, bool isPaused, IJobDetail? jobDetail)
{
    if (isRunning) return "Running";
    if (isPaused) return "Paused";
    return jobDetail?.JobDataMap["Status"]?.ToString() ?? "Scheduled";
}
```

#### 2.2 Add Pause Action
```csharp
[HttpPost]
public async Task<IActionResult> PauseJob(string jobName, string jobGroup)
{
    var jobKey = new JobKey(jobName, jobGroup);
    await _scheduler.PauseJob(jobKey);

    TempData["Message"] = $"Job '{jobName}' paused.";
    return RedirectToAction("Index");
}
```

#### 2.3 Add Resume Action
```csharp
[HttpPost]
public async Task<IActionResult> ResumeJob(string jobName, string jobGroup)
{
    var jobKey = new JobKey(jobName, jobGroup);
    await _scheduler.ResumeJob(jobKey);

    TempData["Message"] = $"Job '{jobName}' resumed.";
    return RedirectToAction("Index");
}
```

#### 2.4 Add Cancel Action
```csharp
[HttpPost]
public async Task<IActionResult> CancelJob(string jobName, string jobGroup)
{
    var jobKey = new JobKey(jobName, jobGroup);
    var interrupted = await _scheduler.Interrupt(jobKey);

    if (interrupted)
        TempData["Message"] = $"Job '{jobName}' cancellation requested.";
    else
        TempData["Error"] = $"Job '{jobName}' could not be interrupted (not running or not interruptable).";

    return RedirectToAction("Index");
}
```

### Phase 3: Update Jobs to Support Cancellation

For jobs to be cancellable, they must check `CancellationToken` (Quartz 3.x pattern):

**Example update for `VisitCleanupJob.cs`:**
```csharp
public async Task Execute(IJobExecutionContext context)
{
    var cancellationToken = context.CancellationToken;

    // Check before long operations
    cancellationToken.ThrowIfCancellationRequested();

    var staleVisits = await _dbContext.PlaceVisitEvents
        .Where(v => v.EndedAtUtc == null)
        .Where(v => v.LastSeenAtUtc < cutoff)
        .ToListAsync(cancellationToken);  // Pass token to async operations

    foreach (var visit in staleVisits)
    {
        cancellationToken.ThrowIfCancellationRequested();  // Check in loops
        visit.EndedAtUtc = visit.LastSeenAtUtc;
    }
    // ...
}
```

**Jobs to update:**
- `LogCleanupJob.cs`
- `AuditLogCleanupJob.cs`
- `VisitCleanupJob.cs`
- `LocationImportJob.cs` (already uses CancellationToken)

### Phase 4: Update View

**File:** `Areas/Admin/Views/Jobs/Index.cshtml`

Update the Status column to show badges:
```html
<td>
    @if (job.IsRunning)
    {
        <span class="badge bg-success">Running</span>
    }
    else if (job.IsPaused)
    {
        <span class="badge bg-warning text-dark">Paused</span>
    }
    else
    {
        <span class="badge bg-secondary">@job.Status</span>
    }
</td>
```

Update the Actions column with conditional buttons:
```html
<td>
    <div class="btn-group" role="group">
        @if (job.IsRunning)
        {
            @if (job.IsInterruptable)
            {
                <form method="post" asp-action="CancelJob" class="d-inline">
                    <input type="hidden" name="jobName" value="@job.JobName" />
                    <input type="hidden" name="jobGroup" value="@job.JobGroup" />
                    <button type="submit" class="btn btn-danger btn-sm"
                            onclick="return confirm('Cancel this running job?')">
                        Cancel
                    </button>
                </form>
            }
            else
            {
                <button class="btn btn-secondary btn-sm" disabled
                        title="Job does not support cancellation">
                    Cancel
                </button>
            }
        }
        else if (job.IsPaused)
        {
            <form method="post" asp-action="ResumeJob" class="d-inline">
                <input type="hidden" name="jobName" value="@job.JobName" />
                <input type="hidden" name="jobGroup" value="@job.JobGroup" />
                <button type="submit" class="btn btn-success btn-sm">
                    Resume
                </button>
            </form>
        }
        else
        {
            <form method="post" asp-action="StartJob" class="d-inline">
                <input type="hidden" name="jobName" value="@job.JobName" />
                <input type="hidden" name="jobGroup" value="@job.JobGroup" />
                <button type="submit" class="btn btn-primary btn-sm">
                    Start
                </button>
            </form>
            <form method="post" asp-action="PauseJob" class="d-inline ms-1">
                <input type="hidden" name="jobName" value="@job.JobName" />
                <input type="hidden" name="jobGroup" value="@job.JobGroup" />
                <button type="submit" class="btn btn-warning btn-sm">
                    Pause
                </button>
            </form>
        }
    </div>
</td>
```

---

## Important Considerations

### 1. Pause vs Cancel
- **Pause**: Stops future scheduled executions. If job is currently running, it continues until completion.
- **Cancel/Interrupt**: Attempts to stop a currently running job. Job must cooperate by checking `CancellationToken`.

### 2. Persistence
Quartz with `JobStoreTX` (AdoJobStore) persists pause state. Paused jobs remain paused across app restarts.

### 3. Misfire Handling
When a paused job is resumed, if it missed scheduled fires, the trigger's misfire instruction determines behavior:
- `WithMisfireHandlingInstructionFireNow()` - Fire immediately
- `WithMisfireHandlingInstructionDoNothing()` - Skip missed fires
- Current jobs use `WithSimpleSchedule(x => x.RepeatForever())` which defaults to smart policy

### 4. Running Job Detection
`GetCurrentlyExecutingJobs()` returns jobs across the cluster (if using clustered mode). This is accurate for single-node deployments.

---

## Acceptance Criteria

- [ ] ViewModel includes `IsRunning`, `IsPaused`, `IsInterruptable` properties
- [ ] Admin can pause a scheduled job (prevents future executions)
- [ ] Admin can resume a paused job
- [ ] Admin can cancel a running job (if job supports it)
- [ ] UI shows current state with visual badges (Running/Paused/Scheduled)
- [ ] UI shows appropriate action buttons based on state
- [ ] Cancel button is disabled for non-interruptable jobs
- [ ] All maintenance jobs (`LogCleanupJob`, `AuditLogCleanupJob`, `VisitCleanupJob`) support cancellation
- [ ] Pause state persists across app restarts
- [ ] Tests cover pause/resume/cancel actions

---

## References

- [Quartz.NET IScheduler.cs](https://github.com/quartznet/quartznet/blob/main/src/Quartz/IScheduler.cs)
- [Making Quartz.NET Jobs Cancellable](https://www.linkedin.com/pulse/making-quartznet-jobs-cancellable-stefano-mioli)
- [Stopping asynchronous Jobs of Quartz 3](https://blexin.com/en/blog-en/stopping-asynchronous-jobs-of-quartz-3/)
- [Quartz.NET Tutorial - Jobs and Triggers](https://www.quartz-scheduler.net/documentation/quartz-2.x/tutorial/jobs-and-triggers.html)
