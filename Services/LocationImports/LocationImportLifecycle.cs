using Microsoft.EntityFrameworkCore;
using Quartz;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationImports;

public enum LocationImportCommandCode { Accepted, ProjectionPending, NotFound, InvalidState, ExecutionActive }
public sealed record LocationImportCommandResult(LocationImportCommandCode Code)
{
    public bool Succeeded => Code is LocationImportCommandCode.Accepted or LocationImportCommandCode.ProjectionPending;
}
public enum LocationImportExecutionOutcome { Completed, Cancelled, Failed, StagedFileUnavailable, Stale }

/// <summary>Observes exact import persistence boundaries without granting or bypassing authority.</summary>
internal interface ILocationImportLifecycleObserver
{
    Task AfterBatchCommittedAsync(int importId, int epoch, int processed, CancellationToken token);
    Task BeforeTerminalPersistenceAsync(
        int importId, int epoch, LocationImportExecutionOutcome outcome, CancellationToken token);
    Task BeforeFileDeletionAsync(int importId, string filePath, CancellationToken token) => Task.CompletedTask;
}

/// <summary>Leaves production import execution unchanged when no test observer is supplied.</summary>
internal sealed class NullLocationImportLifecycleObserver : ILocationImportLifecycleObserver
{
    internal static readonly NullLocationImportLifecycleObserver Instance = new();
    private NullLocationImportLifecycleObserver() { }
    public Task AfterBatchCommittedAsync(int importId, int epoch, int processed, CancellationToken token) =>
        Task.CompletedTask;
    public Task BeforeTerminalPersistenceAsync(
        int importId, int epoch, LocationImportExecutionOutcome outcome, CancellationToken token) =>
        Task.CompletedTask;
}

/// <summary>Maps bounded import execution outcomes to operational history values.</summary>
public static class LocationImportJobOutcome
{
    public static string ToHistoryStatus(LocationImportExecutionOutcome outcome) => outcome switch
    {
        LocationImportExecutionOutcome.Completed => "Completed",
        LocationImportExecutionOutcome.Failed or LocationImportExecutionOutcome.StagedFileUnavailable => "Failed",
        _ => "Cancelled"
    };
}

public interface ILocationImportLifecycle
{
    Task<LocationImportCommandResult> StartAsync(string userId, int importId, CancellationToken cancellationToken = default);
    Task<LocationImportCommandResult> StopAsync(string userId, int importId, CancellationToken cancellationToken = default);
    Task<LocationImportCommandResult> DeleteAsync(string userId, int importId, CancellationToken cancellationToken = default);
    Task<LocationImportExecutionOutcome> ConvergeExecutionAsync(int importId, int epoch,
        LocationImportExecutionOutcome outcome, CancellationToken cancellationToken = default);
}

/// <summary>Owns short relational lifecycle mutations and projects them only after commit.</summary>
public sealed class LocationImportLifecycle(
    IDbContextFactory<ApplicationDbContext> contexts, IScheduler scheduler, ILogger<LocationImportLifecycle> logger,
    LocationImportProjectionCoordinator? projectionCoordinator = null) : ILocationImportLifecycle
{
    private readonly LocationImportProjectionCoordinator projectionCoordinator =
        projectionCoordinator ?? LocationImportProjectionCoordinator.Shared;
    private readonly SemaphoreSlim _commands = new(1, 1);
    private ILocationImportLifecycleObserver _observer = NullLocationImportLifecycleObserver.Instance;

    /// <summary>Creates a lifecycle with a test-controlled, authority-neutral persistence observer.</summary>
    internal LocationImportLifecycle(IDbContextFactory<ApplicationDbContext> contexts, IScheduler scheduler,
        ILogger<LocationImportLifecycle> logger, ILocationImportLifecycleObserver observer)
        : this(contexts, scheduler, logger, (LocationImportProjectionCoordinator?)null) => _observer = observer;

    /// <summary>Creates a lifecycle with test-controlled coordination and authority-neutral observation.</summary>
    internal LocationImportLifecycle(IDbContextFactory<ApplicationDbContext> contexts, IScheduler scheduler,
        ILogger<LocationImportLifecycle> logger, LocationImportProjectionCoordinator projectionCoordinator,
        ILocationImportLifecycleObserver observer)
        : this(contexts, scheduler, logger, projectionCoordinator) => _observer = observer;

    public async Task<LocationImportCommandResult> StartAsync(
        string userId, int importId, CancellationToken cancellationToken = default)
    {
        await _commands.WaitAsync(cancellationToken);
        int epoch;
        try
        {
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            var import = await OwnedAsync(db, userId, importId, cancellationToken);
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
            if (await SaveConvergentlyAsync(db, cancellationToken)) epoch = import.ExecutionEpoch;
            else
            {
                await using var reload = await contexts.CreateDbContextAsync(cancellationToken);
                var current = await OwnedAsync(reload, userId, importId, cancellationToken);
                if (current is null) return new(LocationImportCommandCode.NotFound);
                if (current.DeletionRequestedAtUtc.HasValue || current.Status != ImportStatus.InProgress)
                    return new(LocationImportCommandCode.InvalidState);
                epoch = current.ExecutionEpoch;
            }
        }
        finally { _commands.Release(); }

        try
        {
            await using var projection = await projectionCoordinator.AcquireAsync(importId, cancellationToken);
            var authority = await LoadAuthorityAsync(importId, cancellationToken);
            if (authority is null || authority.ExecutionEpoch != epoch || authority.Status != ImportStatus.InProgress
                || authority.StopRequestedAtUtc.HasValue || authority.DeletionRequestedAtUtc.HasValue)
                return new(LocationImportCommandCode.InvalidState);
            await EnsureProjectionAsync(importId, epoch, cancellationToken);
            authority = await LoadAuthorityAsync(importId, cancellationToken);
            if (authority is null || authority.ExecutionEpoch != epoch || authority.Status != ImportStatus.InProgress
                || authority.StopRequestedAtUtc.HasValue || authority.DeletionRequestedAtUtc.HasValue)
            {
                _ = await scheduler.DeleteJob(LocationImportSchedulerKeys.Job(importId, epoch), cancellationToken);
                return new(LocationImportCommandCode.InvalidState);
            }
            await MarkProjectedAsync(importId, epoch, cancellationToken);
            return new(LocationImportCommandCode.Accepted);
        }
        catch (Exception exception) when (exception is SchedulerException or ObjectAlreadyExistsException)
        {
            logger.LogWarning("Location import scheduling requires reconciliation; code {Code}; " +
                "import {ImportId}; epoch {Epoch}.", "location-import-scheduling-reconciliation-required", importId, epoch);
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
            await using var db = await contexts.CreateDbContextAsync(cancellationToken);
            var import = await OwnedAsync(db, userId, importId, cancellationToken);
            if (import is null) return new(LocationImportCommandCode.NotFound);
            if (import.DeletionRequestedAtUtc.HasValue) return new(LocationImportCommandCode.InvalidState);
            if (import.Status is not null && import.Status != ImportStatus.InProgress && import.Status != ImportStatus.Stopping)
                return new(LocationImportCommandCode.InvalidState);
            import.Status = ImportStatus.Stopping;
            import.StopRequestedAtUtc ??= DateTime.UtcNow;
            import.ProjectionPending = true;
            epoch = import.ExecutionEpoch;
            if (!await SaveConvergentlyAsync(db, cancellationToken))
            {
                await using var reload = await contexts.CreateDbContextAsync(cancellationToken);
                var current = await OwnedAsync(reload, userId, importId, cancellationToken);
                if (current is null) return new(LocationImportCommandCode.NotFound);
                if (current.Status != ImportStatus.Stopping) return new(LocationImportCommandCode.InvalidState);
                epoch = current.ExecutionEpoch;
            }
        }
        finally { _commands.Release(); }

        try
        {
            await using var projection = await projectionCoordinator.AcquireAsync(importId, cancellationToken);
            _ = await scheduler.Interrupt(LocationImportSchedulerKeys.Job(importId, epoch), cancellationToken);
        }
        catch (SchedulerException)
        {
            logger.LogWarning("Location import stop requires reconciliation; code {Code}; import {ImportId}.",
                "location-import-stop-reconciliation-required", importId);
        }
        return new(LocationImportCommandCode.Accepted);
    }

    public async Task<LocationImportCommandResult> DeleteAsync(
        string userId, int importId, CancellationToken cancellationToken = default)
    {
        var initial = await LoadOwnedAuthorityAsync(userId, importId, cancellationToken);
        if (initial is null) return new(LocationImportCommandCode.NotFound);
        if (initial.Status == ImportStatus.InProgress || initial.Status == ImportStatus.Stopping)
            return new(LocationImportCommandCode.ExecutionActive);
        try
        {
            await using var projection = await projectionCoordinator.AcquireAsync(importId, cancellationToken);
            var intent = await CommitDeletionIntentAsync(userId, importId, cancellationToken);
            if (intent.Result is not null) return intent.Result;
            var deletionEpoch = intent.Authority!.Epoch;
            var executing = (await scheduler.GetCurrentlyExecutingJobs(cancellationToken) ?? [])
                .Select(context => context.JobDetail.Key)
                .Any(key => key.Group == LocationImportSchedulerKeys.Group
                    && key.Name.StartsWith($"LocationImportJob_{importId}_", StringComparison.Ordinal));
            if (executing) return new(LocationImportCommandCode.ProjectionPending);
            var keys = await scheduler.GetJobKeys(Quartz.Impl.Matchers.GroupMatcher<JobKey>.GroupEquals(LocationImportSchedulerKeys.Group), cancellationToken)
                ?? new HashSet<JobKey>();
            foreach (var key in keys.Where(key => key.Name.StartsWith($"LocationImportJob_{importId}_", StringComparison.Ordinal)))
            {
                var cleanup = await DeleteProjectionAsync(key, cancellationToken);
                if (cleanup is not QuartzCleanupResult.Removed and not QuartzCleanupResult.AlreadyAbsent)
                    return new(LocationImportCommandCode.ProjectionPending);
            }
            var remaining = await scheduler.GetJobKeys(
                Quartz.Impl.Matchers.GroupMatcher<JobKey>.GroupEquals(LocationImportSchedulerKeys.Group),
                cancellationToken) ?? new HashSet<JobKey>();
            if (remaining.Any(key => key.Name.StartsWith(
                $"LocationImportJob_{importId}_", StringComparison.Ordinal)))
                return new(LocationImportCommandCode.ProjectionPending);
            var authority = await LoadOwnedAuthorityAsync(userId, importId, cancellationToken);
            if (authority is null) return new(LocationImportCommandCode.Accepted);
            if (!authority.DeletionRequestedAtUtc.HasValue || authority.Epoch != deletionEpoch
                || authority.Status == ImportStatus.InProgress || authority.Status == ImportStatus.Stopping)
                return new(LocationImportCommandCode.ProjectionPending);
            await _observer.BeforeFileDeletionAsync(importId, authority.FilePath, cancellationToken);
            if (File.Exists(authority.FilePath)) File.Delete(authority.FilePath);
            await FinalDeleteAsync(userId, importId, deletionEpoch, cancellationToken);
        }
        catch (Exception exception) when (exception is SchedulerException or IOException)
        {
            logger.LogWarning("Import {ImportId} deletion remains pending reconciliation.", importId);
            return new(LocationImportCommandCode.ProjectionPending);
        }
        catch (DbUpdateConcurrencyException)
        {
            var current = await LoadOwnedAuthorityAsync(userId, importId, cancellationToken);
            return current is null || current.DeletionRequestedAtUtc.HasValue
                ? new(LocationImportCommandCode.Accepted)
                : new(LocationImportCommandCode.InvalidState);
        }
        return new(LocationImportCommandCode.Accepted);
    }

    private async Task<QuartzCleanupResult> DeleteProjectionAsync(JobKey key, CancellationToken token)
    {
        try
        {
            return await scheduler.DeleteJob(key, token)
                ? QuartzCleanupResult.Removed : QuartzCleanupResult.AlreadyAbsent;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return QuartzCleanupResult.Cancelled;
        }
        catch (SchedulerException)
        {
            logger.LogWarning("Import projection cleanup remains pending for {JobKey}.", key);
            return QuartzCleanupResult.SchedulerFailed;
        }
    }

    public async Task<LocationImportExecutionOutcome> ConvergeExecutionAsync(
        int importId, int epoch, LocationImportExecutionOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        var authority = await LoadAuthorityAsync(importId, cancellationToken);
        if (authority is null || authority.ExecutionEpoch != epoch) return LocationImportExecutionOutcome.Stale;
        if (authority.DeletionRequestedAtUtc.HasValue) return LocationImportExecutionOutcome.Stale;
        var effectiveOutcome = authority.Status == ImportStatus.Stopping
            ? LocationImportExecutionOutcome.Cancelled : outcome;
        await _observer.BeforeTerminalPersistenceAsync(importId, epoch, effectiveOutcome, cancellationToken);
        await using var db = await contexts.CreateDbContextAsync(cancellationToken);
        var import = await db.LocationImports.SingleOrDefaultAsync(item => item.Id == importId, cancellationToken);
        if (import is null || import.ExecutionEpoch != epoch || import.DeletionRequestedAtUtc.HasValue)
            return LocationImportExecutionOutcome.Stale;
        if (import.Status == ImportStatus.Stopping) effectiveOutcome = LocationImportExecutionOutcome.Cancelled;
        if (effectiveOutcome is LocationImportExecutionOutcome.Cancelled)
            import.Status = ImportStatus.Stopped;
        else if (effectiveOutcome == LocationImportExecutionOutcome.Completed)
            import.Status = ImportStatus.Completed;
        else if (effectiveOutcome is LocationImportExecutionOutcome.Failed or
            LocationImportExecutionOutcome.StagedFileUnavailable)
        {
            import.Status = ImportStatus.Failed;
            import.ErrorMessage = effectiveOutcome == LocationImportExecutionOutcome.StagedFileUnavailable
                ? "Import staged file unavailable." : "Import processing failed.";
        }
        else return LocationImportExecutionOutcome.Stale;
        import.ProjectionPending = false;
        return await SaveConvergentlyAsync(db, cancellationToken)
            ? effectiveOutcome : LocationImportExecutionOutcome.Stale;
    }

    private static Task<LocationImport?> OwnedAsync(ApplicationDbContext db, string userId, int importId, CancellationToken token) =>
        db.LocationImports.SingleOrDefaultAsync(item => item.Id == importId && item.UserId == userId, token);

    private async Task<LocationImport?> LoadAuthorityAsync(int importId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.LocationImports.AsNoTracking().SingleOrDefaultAsync(x => x.Id == importId, token);
    }

    private async Task<OwnedAuthority?> LoadOwnedAuthorityAsync(string userId, int importId, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        return await db.LocationImports.AsNoTracking().Where(x => x.Id == importId && x.UserId == userId)
            .Select(x => new OwnedAuthority(x.ExecutionEpoch, x.Status, x.DeletionRequestedAtUtc, x.FilePath))
            .SingleOrDefaultAsync(token);
    }

    private async Task<(OwnedAuthority? Authority, LocationImportCommandResult? Result)> CommitDeletionIntentAsync(
        string userId, int importId, CancellationToken token)
    {
        {
            await using var db = await contexts.CreateDbContextAsync(token);
            var import = await OwnedAsync(db, userId, importId, token);
            if (import is null) return (null, new(LocationImportCommandCode.NotFound));
            if (import.Status == ImportStatus.InProgress || import.Status == ImportStatus.Stopping)
                return (null, new(LocationImportCommandCode.ExecutionActive));
            import.DeletionRequestedAtUtc ??= DateTime.UtcNow;
            try
            {
                await db.SaveChangesAsync(token);
                return (new(import.ExecutionEpoch, import.Status, import.DeletionRequestedAtUtc, import.FilePath), null);
            }
            catch (DbUpdateConcurrencyException) { }
        }

        var current = await LoadOwnedAuthorityAsync(userId, importId, token);
        if (current is null) return (null, new(LocationImportCommandCode.Accepted));
        if (current.Status == ImportStatus.InProgress || current.Status == ImportStatus.Stopping)
            return (null, new(LocationImportCommandCode.ExecutionActive));
        if (!current.DeletionRequestedAtUtc.HasValue)
            return (null, new(LocationImportCommandCode.InvalidState));
        return (current, null);
    }

    private async Task FinalDeleteAsync(string userId, int importId, int epoch, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var import = await OwnedAsync(db, userId, importId, token);
        if (import is null) return;
        if (!import.DeletionRequestedAtUtc.HasValue || import.ExecutionEpoch != epoch
            || import.Status == ImportStatus.InProgress || import.Status == ImportStatus.Stopping) return;
        db.LocationImports.Remove(import);
        await db.SaveChangesAsync(token);
    }

    internal async Task EnsureProjectionAsync(int importId, int epoch, CancellationToken token)
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

    private async Task MarkProjectedAsync(int importId, int epoch, CancellationToken token)
    {
        await using var db = await contexts.CreateDbContextAsync(token);
        var import = await db.LocationImports.SingleOrDefaultAsync(item => item.Id == importId, token);
        if (import is null || import.ExecutionEpoch != epoch || import.Status != ImportStatus.InProgress) return;
        import.ProjectionPending = false;
        _ = await SaveConvergentlyAsync(db, token);
    }

    private static async Task<bool> SaveConvergentlyAsync(ApplicationDbContext db, CancellationToken token)
    {
        try { await db.SaveChangesAsync(token); return true; }
        catch (DbUpdateConcurrencyException) { db.ChangeTracker.Clear(); return false; }
    }

    private sealed record OwnedAuthority(int Epoch, ImportStatus Status, DateTime? DeletionRequestedAtUtc,
        string FilePath);

    private enum QuartzCleanupResult { Removed, AlreadyAbsent, SchedulerFailed, Cancelled }
}
