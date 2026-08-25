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
    public Task<EnrichmentCommandResult> PauseAsync(string userId, CancellationToken cancellationToken = default)
        => ChangeAsync(userId, (workflow, now) => workflow.TryPause(now, out var reason)
            ? EnrichmentCommandResult.Applied("paused")
            : EnrichmentCommandResult.Invalid(reason!), cancellationToken);

    public async Task<EnrichmentCommandResult> ResumeAsync(string userId, CancellationToken cancellationToken = default)
    {
        var available = await HasCurrentAuthorityAsync(userId, cancellationToken);
        return await ChangeAsync(userId, (workflow, now) => workflow.TryResume(now, available, out var reason)
            ? EnrichmentCommandResult.Applied("scheduled")
            : reason == "authority-unavailable" ? EnrichmentCommandResult.Authority(reason)
            : EnrichmentCommandResult.Invalid(reason!), cancellationToken);
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
        if (!await progressQuery.HasRunnableAsync(
                userId, inspection.Binding, inspection.DatabaseNowUtc, cancellationToken))
            return EnrichmentCommandResult.Conflict("no-candidates");
        var workflow = await db.LocationEnrichmentWorkflows.SingleOrDefaultAsync(
            item => item.UserId == userId, cancellationToken);
        if (workflow is null)
        {
            workflow = LocationEnrichmentWorkflow.Create(userId, DateTime.UtcNow);
            db.Add(workflow);
        }
        workflow.Start(DateTime.UtcNow);
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateException exception) when (IsWorkflowUniqueRace(exception))
        {
            db.ChangeTracker.Clear();
            var current = await db.LocationEnrichmentWorkflows.AsNoTracking()
                .SingleAsync(item => item.UserId == userId, cancellationToken);
            return current.IntentEnabled && current.State is LocationEnrichmentState.Scheduled
                or LocationEnrichmentState.Running
                ? EnrichmentCommandResult.Satisfied("scheduled")
                : EnrichmentCommandResult.Conflict("concurrent-command");
        }
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
            var eligible = EligibleDeferredAttempts(userId, authority);
            var reset = db.Database.IsRelational()
                ? await eligible.ExecuteUpdateAsync(setters => setters
                    .SetProperty(item => item.Outcome, LocationEnrichmentOutcome.None)
                    .SetProperty(item => item.AdmittedAttemptCount, 0)
                    .SetProperty(item => item.NextAttemptAtUtc, now), cancellationToken)
                : await ResetTrackedAsync(eligible, authority, now, cancellationToken);
            if (reset == 0)
                return await RollbackAsync(transaction,
                    EnrichmentCommandResult.Success("nothing-to-retry"), cancellationToken);
            if (!workflow.RetryDeferred(now))
                return await RollbackAsync(transaction,
                    EnrichmentCommandResult.Conflict("concurrent-command"), cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            if (transaction != null) await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (transaction != null) await transaction.RollbackAsync(CancellationToken.None);
            db.ChangeTracker.Clear();
            return EnrichmentCommandResult.Conflict("concurrent-command");
        }
        try { await projection.ProjectAsync(userId, cancellationToken); }
        catch { return EnrichmentCommandResult.Scheduling("scheduling-reconciliation-required"); }
        return EnrichmentCommandResult.Success("scheduled");
    }

    private IQueryable<LocationEnrichmentAttempt> EligibleDeferredAttempts(
        string userId, PersonalProviderAuthorityBinding authority) =>
        db.LocationEnrichmentAttempts.Where(item => item.UserId == userId
            && item.OperationId == null && (item.Outcome == LocationEnrichmentOutcome.NoResult
                || item.Outcome == LocationEnrichmentOutcome.AttemptLimit)
            && item.ProviderKey == authority.ProviderKey && item.ProviderProfileId == authority.ProfileId
            && item.Capability == PersonalProviderCapability.Geocoding
            && item.CredentialGeneration == authority.CredentialGeneration
            && item.ConfigurationGeneration == authority.CapabilityGeneration
            && item.SelectionGeneration == authority.SelectionGeneration
            && item.Verification == authority.Verification
            && item.VerificationCredentialGeneration == authority.VerifiedCredentialGeneration
            && item.VerificationGeneration == authority.VerifiedCapabilityGeneration
            && item.ConsentVersion == authority.ConsentVersion && item.ConsentTimestamp == authority.ConsentedAt
            && item.ConsentCredentialGeneration == authority.ConsentCredentialGeneration
            && item.Location != null && (item.Location.Address == null || item.Location.Address == "")
            && (item.Location.FullAddress == null || item.Location.FullAddress == "")
            && (item.Location.AddressNumber == null || item.Location.AddressNumber == "")
            && (item.Location.StreetName == null || item.Location.StreetName == "")
            && (item.Location.PostCode == null || item.Location.PostCode == "")
            && (item.Location.Place == null || item.Location.Place == "")
            && (item.Location.Region == null || item.Location.Region == "")
            && (item.Location.Country == null || item.Location.Country == "")
            && item.Location.ReverseGeocodingProvider == null
            && item.Location.ReverseGeocodingStorageMode == null && item.Location.ReverseGeocodedAt == null);

    private static bool CanRetryDeferred(LocationEnrichmentWorkflow workflow) => workflow.State is not
        (LocationEnrichmentState.Running or LocationEnrichmentState.Scheduled
        or LocationEnrichmentState.BackingOff or LocationEnrichmentState.PausedByBudget);

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
        return result;
    }

    private async Task<bool> HasCurrentAuthorityAsync(string userId, CancellationToken cancellationToken) =>
        (await providerInspection.InspectPersistentGeocodingAsync(userId, cancellationToken)).Available;

    private async Task<EnrichmentCommandResult> ChangeAsync(string userId,
        Func<LocationEnrichmentWorkflow, DateTime, EnrichmentCommandResult> change,
        CancellationToken cancellationToken)
    {
        var workflow = await db.LocationEnrichmentWorkflows.SingleOrDefaultAsync(
            item => item.UserId == userId, cancellationToken);
        if (workflow is null) return EnrichmentCommandResult.Invalid("missing-workflow");
        var result = change(workflow, DateTime.UtcNow);
        if (result.Classification is not (LocationEnrichmentCommandResult.Applied
            or LocationEnrichmentCommandResult.AlreadySatisfied)) return result;
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            var current = await db.LocationEnrichmentWorkflows.AsNoTracking()
                .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
            return current is null ? EnrichmentCommandResult.Conflict("concurrent-command")
                : ClassifyAfterRace(result.Code, current);
        }
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
public sealed record EnrichmentCommandResult(LocationEnrichmentCommandResult Classification, string Code)
{
    public bool Succeeded => Classification is LocationEnrichmentCommandResult.Applied
        or LocationEnrichmentCommandResult.AlreadySatisfied;
    public static EnrichmentCommandResult Success(string code) => Applied(code);
    public static EnrichmentCommandResult Applied(string code) => new(LocationEnrichmentCommandResult.Applied, code);
    public static EnrichmentCommandResult Satisfied(string code) => new(LocationEnrichmentCommandResult.AlreadySatisfied, code);
    public static EnrichmentCommandResult Conflict(string code) => new(LocationEnrichmentCommandResult.Conflict, code);
    public static EnrichmentCommandResult Invalid(string code) => new(LocationEnrichmentCommandResult.InvalidTransition, code);
    public static EnrichmentCommandResult Authority(string code) => new(LocationEnrichmentCommandResult.AuthorityUnavailable, code);
    public static EnrichmentCommandResult Scheduling(string code) => new(LocationEnrichmentCommandResult.SchedulingPending, code);
}
