using Microsoft.EntityFrameworkCore;
using Quartz;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationImports;

public enum LocationImportCommandCode { Accepted, ProjectionPending, NotFound, InvalidState, ExecutionActive }
public sealed record LocationImportCommandResult(LocationImportCommandCode Code)
{
    public bool Succeeded => Code is LocationImportCommandCode.Accepted or LocationImportCommandCode.ProjectionPending;
}
public enum LocationImportExecutionOutcome { Completed, Cancelled, Failed, Stale }

/// <summary>Maps bounded import execution outcomes to operational history values.</summary>
public static class LocationImportJobOutcome
{
    public static string ToHistoryStatus(LocationImportExecutionOutcome outcome) => outcome switch
    {
        LocationImportExecutionOutcome.Completed => "Completed",
        LocationImportExecutionOutcome.Failed => "Failed",
        _ => "Cancelled"
    };
}

public interface ILocationImportLifecycle
{
    Task<LocationImportCommandResult> StartAsync(string userId, int importId, CancellationToken cancellationToken = default);
    Task<LocationImportCommandResult> StopAsync(string userId, int importId, CancellationToken cancellationToken = default);
    Task<LocationImportCommandResult> DeleteAsync(string userId, int importId, CancellationToken cancellationToken = default);
    Task ConvergeExecutionAsync(int importId, int epoch, LocationImportExecutionOutcome outcome, CancellationToken cancellationToken = default);
}

/// <summary>Owns short relational lifecycle mutations and projects them only after commit.</summary>
public sealed class LocationImportLifecycle(
    ApplicationDbContext db, IScheduler scheduler, ILogger<LocationImportLifecycle> logger) : ILocationImportLifecycle
{
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(int ImportId, int Epoch), SemaphoreSlim>
        ProjectionLocks = new();
    private readonly SemaphoreSlim _commands = new(1, 1);

    public async Task<LocationImportCommandResult> StartAsync(
        string userId, int importId, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken);
        int epoch;
        try
        {
            var import = await OwnedAsync(userId, importId, cancellationToken);
            if (import is null) return new(LocationImportCommandCode.NotFound);
            if (import.DeletionRequestedAtUtc.HasValue) return new(LocationImportCommandCode.InvalidState);
            if (import.Status == ImportStatus.Stopping) return new(LocationImportCommandCode.InvalidState);
            if (import.Status != ImportStatus.InProgress)
            {
                import.ExecutionEpoch++;
                import.Status = ImportStatus.InProgress;
                import.StopRequestedAtUtc = null;
                import.ErrorMessage = null;
            }
            import.ProjectionPending = true;
            if (await SaveConvergentlyAsync(cancellationToken)) epoch = import.ExecutionEpoch;
            else
            {
                var current = await OwnedAsync(userId, importId, cancellationToken);
                if (current is null) return new(LocationImportCommandCode.NotFound);
                if (current.DeletionRequestedAtUtc.HasValue || current.Status != ImportStatus.InProgress)
                    return new(LocationImportCommandCode.InvalidState);
                epoch = current.ExecutionEpoch;
            }
        }
        finally { _commands.Release(); }

        try
        {
            await EnsureProjectionAsync(importId, epoch, cancellationToken);
            await MarkProjectedAsync(importId, epoch, cancellationToken);
            return new(LocationImportCommandCode.Accepted);
        }
        catch (Exception exception) when (exception is SchedulerException or ObjectAlreadyExistsException)
        {
            logger.LogWarning(exception, "Import {ImportId} epoch {Epoch} remains pending Quartz projection.", importId, epoch);
            return new(LocationImportCommandCode.ProjectionPending);
        }
    }

    public async Task<LocationImportCommandResult> StopAsync(
        string userId, int importId, CancellationToken cancellationToken = default)
    {
        int epoch;
        await _commands.WaitAsync(cancellationToken);
        try
        {
            var import = await OwnedAsync(userId, importId, cancellationToken);
            if (import is null) return new(LocationImportCommandCode.NotFound);
            if (import.DeletionRequestedAtUtc.HasValue) return new(LocationImportCommandCode.InvalidState);
            if (import.Status is not null && import.Status != ImportStatus.InProgress && import.Status != ImportStatus.Stopping)
                return new(LocationImportCommandCode.InvalidState);
            import.Status = ImportStatus.Stopping;
            import.StopRequestedAtUtc ??= DateTime.UtcNow;
            import.ProjectionPending = true;
            epoch = import.ExecutionEpoch;
            if (!await SaveConvergentlyAsync(cancellationToken))
            {
                var current = await OwnedAsync(userId, importId, cancellationToken);
                if (current is null) return new(LocationImportCommandCode.NotFound);
                if (current.Status != ImportStatus.Stopping) return new(LocationImportCommandCode.InvalidState);
                epoch = current.ExecutionEpoch;
            }
        }
        finally { _commands.Release(); }

        try { _ = await scheduler.Interrupt(LocationImportSchedulerKeys.Job(importId, epoch), cancellationToken); }
        catch (SchedulerException exception)
        {
            logger.LogWarning(exception, "Import {ImportId} stop interruption remains pending.", importId);
        }
        return new(LocationImportCommandCode.Accepted);
    }

    public async Task<LocationImportCommandResult> DeleteAsync(
        string userId, int importId, CancellationToken cancellationToken = default)
    {
        var import = await OwnedAsync(userId, importId, cancellationToken);
        if (import is null) return new(LocationImportCommandCode.NotFound);
        if (import.Status == ImportStatus.InProgress || import.Status == ImportStatus.Stopping)
            return new(LocationImportCommandCode.ExecutionActive);

        var path = import.FilePath;
        import.DeletionRequestedAtUtc ??= DateTime.UtcNow;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var current = await OwnedAsync(userId, importId, cancellationToken);
            if (current is null) return new(LocationImportCommandCode.Accepted);
            if (current.Status == ImportStatus.InProgress || current.Status == ImportStatus.Stopping)
                return new(LocationImportCommandCode.ExecutionActive);
            if (!current.DeletionRequestedAtUtc.HasValue) return new(LocationImportCommandCode.InvalidState);
            import = current;
            path = current.FilePath;
        }
        try
        {
            var executing = (await scheduler.GetCurrentlyExecutingJobs(cancellationToken) ?? [])
                .Select(context => context.JobDetail.Key)
                .Any(key => key.Group == LocationImportSchedulerKeys.Group
                    && key.Name.StartsWith($"LocationImportJob_{importId}_", StringComparison.Ordinal));
            if (executing) return new(LocationImportCommandCode.ProjectionPending);
            var keys = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.GroupEquals(LocationImportSchedulerKeys.Group), cancellationToken)
                ?? new HashSet<JobKey>();
            foreach (var key in keys.Where(key => key.Name.StartsWith($"LocationImportJob_{importId}_", StringComparison.Ordinal)))
                await scheduler.DeleteJob(key, cancellationToken);
            if (File.Exists(path)) File.Delete(path);
            db.LocationImports.Remove(import);
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is SchedulerException or IOException)
        {
            logger.LogWarning(exception, "Import {ImportId} deletion remains pending reconciliation.", importId);
            return new(LocationImportCommandCode.ProjectionPending);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var current = await OwnedAsync(userId, importId, cancellationToken);
            return current is null || current.DeletionRequestedAtUtc.HasValue
                ? new(LocationImportCommandCode.Accepted)
                : new(LocationImportCommandCode.InvalidState);
        }
        return new(LocationImportCommandCode.Accepted);
    }

    public async Task ConvergeExecutionAsync(int importId, int epoch, LocationImportExecutionOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        db.ChangeTracker.Clear();
        var import = await db.LocationImports.SingleOrDefaultAsync(item => item.Id == importId, cancellationToken);
        if (import is null || import.ExecutionEpoch != epoch) return;
        if (import.DeletionRequestedAtUtc.HasValue) return;
        if (import.Status == ImportStatus.Stopping || outcome is LocationImportExecutionOutcome.Cancelled)
            import.Status = ImportStatus.Stopped;
        else if (outcome == LocationImportExecutionOutcome.Completed)
            import.Status = ImportStatus.Completed;
        else if (outcome == LocationImportExecutionOutcome.Failed)
        {
            import.Status = ImportStatus.Failed;
            import.ErrorMessage = "Import processing failed.";
        }
        else return;
        import.ProjectionPending = false;
        _ = await SaveConvergentlyAsync(cancellationToken);
    }

    private Task<LocationImport?> OwnedAsync(string userId, int importId, CancellationToken token) =>
        db.LocationImports.SingleOrDefaultAsync(item => item.Id == importId && item.UserId == userId, token);

    internal async Task EnsureProjectionAsync(int importId, int epoch, CancellationToken token)
    {
        var gate = ProjectionLocks.GetOrAdd((importId, epoch), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(token);
        try
        {
            var jobKey = LocationImportSchedulerKeys.Job(importId, epoch);
            var triggerKey = LocationImportSchedulerKeys.Trigger(importId, epoch);
            if (!await scheduler.CheckExists(jobKey, token))
            {
                try
                {
                    await scheduler.ScheduleJob(LocationImportSchedulerKeys.BuildJob(importId, epoch),
                        LocationImportSchedulerKeys.BuildTrigger(importId, epoch), token);
                }
                catch (ObjectAlreadyExistsException) { }
            }
            else if (!await scheduler.CheckExists(triggerKey, token))
                await scheduler.ScheduleJob(LocationImportSchedulerKeys.BuildTrigger(importId, epoch), token);
        }
        finally { gate.Release(); }
    }

    private async Task MarkProjectedAsync(int importId, int epoch, CancellationToken token)
    {
        db.ChangeTracker.Clear();
        var import = await db.LocationImports.SingleOrDefaultAsync(item => item.Id == importId, token);
        if (import is null || import.ExecutionEpoch != epoch || import.Status != ImportStatus.InProgress) return;
        import.ProjectionPending = false;
        _ = await SaveConvergentlyAsync(token);
    }

    private async Task<bool> SaveConvergentlyAsync(CancellationToken token)
    {
        try { await db.SaveChangesAsync(token); return true; }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return false; }
    }
}
