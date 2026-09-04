using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Turns committed import opt-in into the user's single durable workflow intent.</summary>
public interface IImportEnrichmentHandoff
{
    Task EnsureAsync(string userId, CancellationToken cancellationToken = default);
    Task<EnrichmentCommandResult> StartAsync(string userId, CancellationToken cancellationToken = default);
    Task<EnrichmentCommandResult> RetryDeferredAsync(string userId, CancellationToken cancellationToken = default);
    Task<EnrichmentCommandResult> RepairIncompleteAsync(string userId, CancellationToken cancellationToken = default);
    Task<EnrichmentCommandResult> PauseAsync(string userId, CancellationToken cancellationToken = default);
    Task<EnrichmentCommandResult> ResumeAsync(string userId, CancellationToken cancellationToken = default);
    Task<EnrichmentCommandResult> CancelAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>Creates or resumes relational intent before projecting one Quartz trigger.</summary>
public sealed class ImportEnrichmentHandoff(
    ApplicationDbContext db, IWorkflowScheduleProjection projection,
    IPersonalProviderStatusReader providerInspection,
    ILocationEnrichmentProgressQuery progressQuery) : IImportEnrichmentHandoff
{
    private const int RepairCommandLimit = 1_000;
    /// <summary>Test-only coordination point after advisory inspection and before locked validation.</summary>
    internal Func<CancellationToken, Task> BeforeTransactionalAuthorityValidationAsync { get; set; }
        = static _ => Task.CompletedTask;

    public Task<EnrichmentCommandResult> PauseAsync(string userId, CancellationToken cancellationToken = default)
        => ChangeAsync(userId, (workflow, now) => workflow.TryPause(now, out var reason)
            ? EnrichmentCommandResult.Applied("paused")
            : EnrichmentCommandResult.Invalid(reason!), cancellationToken);

    public async Task<EnrichmentCommandResult> ResumeAsync(string userId, CancellationToken cancellationToken = default)
    {
        var inspection = await providerInspection.InspectPersistentGeocodingAsync(userId, cancellationToken);
        if (!inspection.Available || inspection.Binding is null)
            return EnrichmentCommandResult.Authority("authority-unavailable");
        await BeforeTransactionalAuthorityValidationAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var workflow = await LockWorkflowAsync(userId, cancellationToken);
        if (workflow is null)
            return await RollbackAsync(transaction, EnrichmentCommandResult.Invalid("missing-workflow"), cancellationToken);
        if (workflow.State is not (LocationEnrichmentState.PausedByUser or LocationEnrichmentState.PausedByAuthority))
            return await RollbackAsync(transaction, EnrichmentCommandResult.Invalid("invalid-state"), cancellationToken);
        if (!await AuthorityIsCurrentAsync(userId, inspection.Binding, cancellationToken))
            return await RollbackAsync(transaction, EnrichmentCommandResult.Authority("authority-unavailable"), cancellationToken);
        var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
        if (!await progressQuery.HasRunnableAsync(userId, inspection.Binding, now, cancellationToken))
            return await RollbackAsync(transaction, EnrichmentCommandResult.Conflict("no-candidates"), cancellationToken);
        if (!workflow.TryResume(now, true, out var reason))
            return await RollbackAsync(transaction, EnrichmentCommandResult.Invalid(reason!), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        if (transaction != null) await transaction.DisposeAsync();
        try { await projection.ProjectAsync(userId, cancellationToken); }
        catch { return EnrichmentCommandResult.Scheduling("scheduling-reconciliation-required"); }
        return EnrichmentCommandResult.Applied("scheduled");
    }

    public Task<EnrichmentCommandResult> CancelAsync(string userId, CancellationToken cancellationToken = default)
        => ChangeAsync(userId, (workflow, now) =>
        {
            if (workflow.State == LocationEnrichmentState.Cancelled)
                return EnrichmentCommandResult.Satisfied("cancelled");
            workflow.Cancel(now);
            return EnrichmentCommandResult.Applied("cancelled");
        }, cancellationToken);

    public async Task EnsureAsync(string userId, CancellationToken cancellationToken = default)
    {
        if (await HasCurrentAuthorityAsync(userId, cancellationToken))
        {
            _ = await StartAsync(userId, cancellationToken);
            return;
        }
        var workflow = await db.LocationEnrichmentWorkflows.SingleOrDefaultAsync(
            item => item.UserId == userId, cancellationToken);
        if (workflow is null)
        {
            workflow = LocationEnrichmentWorkflow.Create(userId, DateTime.UtcNow);
            db.Add(workflow);
        }
        workflow.Start(DateTime.UtcNow);
        workflow.PauseForAuthority(LocationEnrichmentOutcome.AuthorityUnavailable, DateTime.UtcNow);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsWorkflowUniqueRace(exception))
        {
            db.ChangeTracker.Clear();
            return;
        }
        try { await projection.ProjectAsync(userId, cancellationToken); } catch { }
    }

    /// <summary>Commits explicit intent only when current authority and candidates permit a run.</summary>
    public async Task<EnrichmentCommandResult> StartAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        var inspection = await providerInspection.InspectPersistentGeocodingAsync(userId, cancellationToken);
        if (!inspection.Available || inspection.Binding is null)
            return EnrichmentCommandResult.Conflict("authority-unavailable");
        await BeforeTransactionalAuthorityValidationAsync(cancellationToken);
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        await LockWorkflowCreationAuthorityAsync(userId, cancellationToken);
        var workflow = await LockWorkflowAsync(userId, cancellationToken);
        if (workflow is { IntentEnabled: true, State: LocationEnrichmentState.Scheduled or LocationEnrichmentState.Running })
            return await RollbackAsync(transaction, EnrichmentCommandResult.Satisfied("scheduled"), cancellationToken);
        if (!await AuthorityIsCurrentAsync(userId, inspection.Binding, cancellationToken))
            return await RollbackAsync(transaction,
                EnrichmentCommandResult.Authority("authority-unavailable"), cancellationToken);
        var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
        if (!await progressQuery.HasRunnableAsync(userId, inspection.Binding, now, cancellationToken))
            return await RollbackAsync(transaction, EnrichmentCommandResult.Conflict("no-candidates"), cancellationToken);
        if (workflow is null)
        {
            workflow = LocationEnrichmentWorkflow.Create(userId, now);
            db.Add(workflow);
        }
        workflow.Start(now);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsWorkflowUniqueRace(exception))
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            if (transaction != null) await transaction.DisposeAsync();
            db.ChangeTracker.Clear();
            var current = await db.LocationEnrichmentWorkflows.AsNoTracking()
                .SingleAsync(item => item.UserId == userId, cancellationToken);
            return current.IntentEnabled && current.State is LocationEnrichmentState.Scheduled
                or LocationEnrichmentState.Running
                ? EnrichmentCommandResult.Satisfied("scheduled")
                : EnrichmentCommandResult.Conflict("concurrent-command");
        }
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        if (transaction != null) await transaction.DisposeAsync();
        try { await projection.ProjectAsync(userId, cancellationToken); }
        catch { return EnrichmentCommandResult.Conflict("scheduling-reconciliation-required"); }
        return EnrichmentCommandResult.Success("scheduled");
    }

    /// <summary>Explicitly resets only deferred rows still eligible under current provider authority.</summary>
    public async Task<EnrichmentCommandResult> RetryDeferredAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        var inspection = await providerInspection.InspectPersistentGeocodingAsync(userId, cancellationToken);
        var authority = inspection.Binding;
        if (!inspection.Available || authority is null)
            return EnrichmentCommandResult.Conflict("authority-unavailable");
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var reset = 0;
        try
        {
            var workflow = await LockWorkflowAsync(userId, cancellationToken);
            if (workflow is null || !CanRetryDeferred(workflow))
                return await RollbackAsync(transaction,
                    EnrichmentCommandResult.Conflict("invalid-state"), cancellationToken);
            if (!await AuthorityIsCurrentAsync(userId, authority, cancellationToken))
                return await RollbackAsync(transaction,
                    EnrichmentCommandResult.Conflict("authority-unavailable"), cancellationToken);
            var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
            var eligible = LocationEnrichmentProgressQuery.ManuallyRetryableAttempts(db, userId, authority);
            reset = db.Database.IsRelational()
                ? await eligible.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Outcome, LocationEnrichmentOutcome.None)
                    .SetProperty(item => item.AdmittedAttemptCount, 0)
                    .SetProperty(item => item.NextAttemptAtUtc, now), cancellationToken)
                : await ResetTrackedAsync(eligible, authority, now, cancellationToken);
            if (reset == 0)
                return await RollbackAsync(transaction,
                    EnrichmentCommandResult.Satisfied("nothing-to-retry"), cancellationToken);
            if (!workflow.RetryDeferred(now))
                return await RollbackAsync(transaction,
                    EnrichmentCommandResult.Conflict("concurrent-command"), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
            if (transaction != null) await transaction.DisposeAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            if (transaction != null) await transaction.DisposeAsync();
            db.ChangeTracker.Clear();
            return EnrichmentCommandResult.Conflict("concurrent-command");
        }
        try { await projection.ProjectAsync(userId, cancellationToken); }
        catch { return EnrichmentCommandResult.Scheduling("scheduling-reconciliation-required", reset); }
        return EnrichmentCommandResult.Success("scheduled", reset);
    }

    /// <summary>Creates explicit repair intent for current Geoapify-persistent partial addresses.</summary>
    public async Task<EnrichmentCommandResult> RepairIncompleteAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        var inspection = await providerInspection.InspectPersistentGeocodingAsync(userId, cancellationToken);
        var authority = inspection.Binding;
        if (!inspection.Available || authority is null || authority.ProviderKey != "geoapify")
            return EnrichmentCommandResult.Authority("authority-unavailable");
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var workflow = await LockWorkflowAsync(userId, cancellationToken);
        if (workflow is null || !CanRetryDeferred(workflow))
            return await RollbackAsync(transaction, EnrichmentCommandResult.Conflict("invalid-state"), cancellationToken);
        if (!await AuthorityIsCurrentAsync(userId, authority, cancellationToken))
            return await RollbackAsync(transaction, EnrichmentCommandResult.Authority("authority-unavailable"), cancellationToken);
        var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
        var eligibleLocations = LocationEnrichmentProgressQuery.IncompleteGeoapifyLocations(db, userId)
            .OrderBy(item => item.Timestamp).ThenBy(item => item.Id).Take(RepairCommandLimit);
        var locations = db.Database.IsNpgsql()
            ? await db.Locations.FromSqlInterpolated($$"""
                SELECT * FROM "Locations"
                WHERE "UserId" = {{userId}}
                  AND ("Place" IS NULL OR btrim("Place") = '')
                  AND "ReverseGeocodingProvider" = 'geoapify'
                  AND "ReverseGeocodingStorageMode" = 'persistent'
                  AND "ReverseGeocodedAt" IS NOT NULL
                  AND (("Address" IS NOT NULL AND btrim("Address") <> '')
                    OR ("FullAddress" IS NOT NULL AND btrim("FullAddress") <> '')
                    OR ("AddressNumber" IS NOT NULL AND btrim("AddressNumber") <> '')
                    OR ("StreetName" IS NOT NULL AND btrim("StreetName") <> '')
                    OR ("PostCode" IS NOT NULL AND btrim("PostCode") <> '')
                    OR ("Region" IS NOT NULL AND btrim("Region") <> '')
                    OR ("Country" IS NOT NULL AND btrim("Country") <> ''))
                ORDER BY "Timestamp", "Id" LIMIT {{RepairCommandLimit}} FOR UPDATE
                """).Select(item => item.Id).ToListAsync(cancellationToken)
            : await eligibleLocations.Select(item => item.Id).ToListAsync(cancellationToken);
        if (locations.Count == 0)
            return await RollbackAsync(transaction, EnrichmentCommandResult.Satisfied("nothing-to-repair"), cancellationToken);
        var existing = await db.LocationEnrichmentAttempts.Where(item => item.UserId == userId
            && locations.Contains(item.LocationId)).ToDictionaryAsync(item => item.LocationId, cancellationToken);
        foreach (var locationId in locations)
        {
            if (!existing.TryGetValue(locationId, out var attempt))
            {
                attempt = new LocationEnrichmentAttempt { UserId = userId, LocationId = locationId };
                db.Add(attempt);
            }
            attempt.PrepareRepair(authority, now);
        }
        if (!workflow.RetryDeferred(now))
            return await RollbackAsync(transaction, EnrichmentCommandResult.Conflict("concurrent-command"), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        if (transaction != null) await transaction.DisposeAsync();
        try { await projection.ProjectAsync(userId, cancellationToken); }
        catch { return EnrichmentCommandResult.Scheduling("scheduling-reconciliation-required", locations.Count); }
        return EnrichmentCommandResult.Success("repair-scheduled", locations.Count);
    }

    private static bool CanRetryDeferred(LocationEnrichmentWorkflow workflow) => workflow.State is
        LocationEnrichmentState.PausedByAuthority
        or LocationEnrichmentState.Completed or LocationEnrichmentState.Failed;

    private async Task<int> ResetTrackedAsync(IQueryable<LocationEnrichmentAttempt> eligible,
        PersonalProviderAuthorityBinding authority, DateTime now, CancellationToken cancellationToken)
    {
        var reset = 0;
        await foreach (var attempt in eligible.AsAsyncEnumerable().WithCancellation(cancellationToken))
        {
            attempt.ResetDeferred(authority.ProviderKey, authority.CredentialGeneration,
                authority.CapabilityGeneration, authority.SelectionGeneration, now);
            reset++;
        }
        return reset;
    }

    private async Task<LocationEnrichmentWorkflow?> LockWorkflowAsync(
        string userId, CancellationToken cancellationToken) => db.Database.IsNpgsql()
        ? await db.LocationEnrichmentWorkflows.FromSqlInterpolated($$"""
            SELECT *, xmin FROM "LocationEnrichmentWorkflows" WHERE "UserId" = {{userId}} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken)
        : await db.LocationEnrichmentWorkflows.SingleOrDefaultAsync(
            item => item.UserId == userId, cancellationToken);

    /// <summary>Serializes the absent-row workflow creation case on the existing user owner.</summary>
    private async Task LockWorkflowCreationAuthorityAsync(string userId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsNpgsql()) return;
        _ = await db.Users.FromSqlInterpolated($$"""
            SELECT * FROM "AspNetUsers" WHERE "Id" = {{userId}} FOR UPDATE
            """).SingleAsync(cancellationToken);
    }

    private async Task<bool> AuthorityIsCurrentAsync(string userId,
        PersonalProviderAuthorityBinding authority, CancellationToken cancellationToken)
    {
        if (!db.Database.IsRelational()) return true;
        var selection = await db.PersonalLocationProviderSelections.FromSqlInterpolated($$"""
            SELECT *, xmin FROM "PersonalLocationProviderSelections" WHERE "UserId" = {{userId}} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        if (selection?.GeocodingProviderKey != authority.ProviderKey
            || selection.GeocodingSelectionGeneration != authority.SelectionGeneration) return false;
        var profile = await db.PersonalLocationProviderProfiles.FromSqlInterpolated($$"""
            SELECT *, xmin FROM "PersonalLocationProviderProfiles"
            WHERE "UserId" = {{userId}} AND "ProviderKey" = {{authority.ProviderKey}} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken);
        return profile is not null && profile.Id == authority.ProfileId && profile.RevokedAt is null
            && profile.GeocodingAuthorized && profile.CredentialGeneration == authority.CredentialGeneration
            && profile.GeocodingGeneration == authority.CapabilityGeneration
            && profile.GeocodingVerification == authority.Verification
            && profile.GeocodingVerifiedCredentialGeneration == authority.VerifiedCredentialGeneration
            && profile.GeocodingVerifiedConfigurationGeneration == authority.VerifiedCapabilityGeneration
            && profile.PermanentGeocodingConsentVersion == authority.ConsentVersion
            && profile.PermanentGeocodingConsentedAt == authority.ConsentedAt
            && profile.PermanentGeocodingConsentCredentialGeneration == authority.ConsentCredentialGeneration;
    }

    private static async Task<EnrichmentCommandResult> RollbackAsync(
        Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? transaction,
        EnrichmentCommandResult result, CancellationToken cancellationToken)
    {
        if (transaction != null) await transaction.RollbackAsync(cancellationToken);
        if (transaction != null) await transaction.DisposeAsync();
        return result;
    }

    private async Task<bool> HasCurrentAuthorityAsync(string userId, CancellationToken cancellationToken) =>
        (await providerInspection.InspectPersistentGeocodingAsync(userId, cancellationToken)).Available;

    private async Task<EnrichmentCommandResult> ChangeAsync(string userId,
        Func<LocationEnrichmentWorkflow, DateTime, EnrichmentCommandResult> change,
        CancellationToken cancellationToken)
    {
        await using var transaction = db.Database.IsRelational()
            ? await db.Database.BeginTransactionAsync(cancellationToken) : null;
        var workflow = await LockWorkflowAsync(userId, cancellationToken);
        if (workflow is null) return EnrichmentCommandResult.Invalid("missing-workflow");
        var now = await LocationEnrichmentExecutionAuthority.DatabaseUtcNowAsync(db, cancellationToken);
        var result = change(workflow, now);
        if (result.Classification is not (LocationEnrichmentCommandResult.Applied
            or LocationEnrichmentCommandResult.AlreadySatisfied))
            return await RollbackAsync(transaction, result, cancellationToken);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            if (transaction != null) await transaction.DisposeAsync();
            db.ChangeTracker.Clear();
            var current = await db.LocationEnrichmentWorkflows.AsNoTracking()
                .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
            return current is null ? EnrichmentCommandResult.Conflict("concurrent-command")
                : ClassifyAfterRace(result.Code, current);
        }
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        if (transaction != null) await transaction.DisposeAsync();
        try { await projection.ProjectAsync(userId, cancellationToken); }
        catch { return EnrichmentCommandResult.Scheduling("scheduling-reconciliation-required"); }
        return result;
    }

    private static EnrichmentCommandResult ClassifyAfterRace(string command,
        LocationEnrichmentWorkflow current) => command switch
    {
        "paused" when current.State == LocationEnrichmentState.PausedByUser
            => EnrichmentCommandResult.Satisfied("paused"),
        "cancelled" when current.State == LocationEnrichmentState.Cancelled
            => EnrichmentCommandResult.Satisfied("cancelled"),
        "scheduled" when current.State == LocationEnrichmentState.Scheduled && current.IntentEnabled
            => EnrichmentCommandResult.Satisfied("scheduled"),
        _ => EnrichmentCommandResult.Conflict("concurrent-command")
    };

    private static bool IsWorkflowUniqueRace(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}

/// <summary>Returns bounded command feedback without provider or scheduler internals.</summary>
public enum LocationEnrichmentCommandResult
{ Applied, AlreadySatisfied, Conflict, InvalidTransition, AuthorityUnavailable, SchedulingPending }

/// <summary>Contains a finite classification and bounded user-safe code.</summary>
public sealed record EnrichmentCommandResult(LocationEnrichmentCommandResult Classification, string Code,
    int AffectedCount = 0)
{
    public bool Succeeded => Classification is LocationEnrichmentCommandResult.Applied
        or LocationEnrichmentCommandResult.AlreadySatisfied;
    public static EnrichmentCommandResult Success(string code, int affectedCount = 0) => Applied(code, affectedCount);
    public static EnrichmentCommandResult Applied(string code, int affectedCount = 0) =>
        new(LocationEnrichmentCommandResult.Applied, code, affectedCount);
    public static EnrichmentCommandResult Satisfied(string code) => new(LocationEnrichmentCommandResult.AlreadySatisfied, code);
    public static EnrichmentCommandResult Conflict(string code) => new(LocationEnrichmentCommandResult.Conflict, code);
    public static EnrichmentCommandResult Invalid(string code) => new(LocationEnrichmentCommandResult.InvalidTransition, code);
    public static EnrichmentCommandResult Authority(string code) => new(LocationEnrichmentCommandResult.AuthorityUnavailable, code);
    public static EnrichmentCommandResult Scheduling(string code, int affectedCount = 0) =>
        new(LocationEnrichmentCommandResult.SchedulingPending, code, affectedCount);
}
