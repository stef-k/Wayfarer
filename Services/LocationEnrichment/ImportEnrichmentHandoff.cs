using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;

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
    ApplicationDbContext db, IWorkflowScheduleProjection projection) : IImportEnrichmentHandoff
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
        if (!await HasCurrentAuthorityAsync(userId, cancellationToken))
            return EnrichmentCommandResult.Conflict("authority-unavailable");
        if (!await HasCandidateAsync(userId, cancellationToken))
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
        var selection = await db.PersonalLocationProviderSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var providerKey = selection?.GeocodingProviderKey;
        var profile = providerKey == null ? null : await db.PersonalLocationProviderProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == providerKey, cancellationToken);
        if (profile == null || selection == null || !profile.GeocodingAuthorized || profile.RevokedAt != null)
            return EnrichmentCommandResult.Conflict("authority-unavailable");
        var attempts = await db.LocationEnrichmentAttempts.Include(item => item.Location)
            .Where(item => item.UserId == userId && (item.Outcome == LocationEnrichmentOutcome.InvalidCoordinates
                || item.Outcome == LocationEnrichmentOutcome.NoResult
                || item.Outcome == LocationEnrichmentOutcome.AttemptLimit))
            .ToListAsync(cancellationToken);
        var eligible = attempts.Where(item => item.Location != null
            && LocationProviders.GeoapifyLocationBackfillService.IsWhollyUnenriched(item.Location)).ToList();
        if (eligible.Count == 0) return EnrichmentCommandResult.Success("nothing-to-retry");
        var now = DateTime.UtcNow;
        foreach (var attempt in eligible)
            attempt.ResetDeferred(providerKey!, profile.CredentialGeneration, profile.GeocodingGeneration,
                selection.GeocodingSelectionGeneration, now);
        var workflow = await db.LocationEnrichmentWorkflows.SingleAsync(item => item.UserId == userId, cancellationToken);
        if (!workflow.RetryDeferred(now)) return EnrichmentCommandResult.Conflict("invalid-state");
        try { await db.SaveChangesAsync(cancellationToken); }
        catch (DbUpdateConcurrencyException)
        {
            db.ChangeTracker.Clear();
            return EnrichmentCommandResult.Conflict("concurrent-command");
        }
        try { await projection.ProjectAsync(userId, cancellationToken); }
        catch { return EnrichmentCommandResult.Conflict("scheduling-reconciliation-required"); }
        return EnrichmentCommandResult.Success("scheduled");
    }

    private async Task<bool> HasCurrentAuthorityAsync(string userId, CancellationToken cancellationToken)
    {
        var selection = await db.PersonalLocationProviderSelections.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var key = selection?.GeocodingProviderKey;
        var profile = key == null ? null : await db.PersonalLocationProviderProfiles.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId && item.ProviderKey == key, cancellationToken);
        return profile is { GeocodingAuthorized: true, RevokedAt: null,
            GeocodingVerification: Models.LocationProviders.PersonalProviderVerification.Verified }
            && profile.GeocodingVerifiedCredentialGeneration == profile.CredentialGeneration
            && profile.GeocodingVerifiedConfigurationGeneration == profile.GeocodingGeneration
            && (key != "mapbox" || profile.HasCurrentPermanentGeocodingConsent());
    }

    private Task<bool> HasCandidateAsync(string userId, CancellationToken cancellationToken) =>
        db.Locations.AnyAsync(item => item.UserId == userId
            && (item.Address == null || item.Address == "") && (item.FullAddress == null || item.FullAddress == "")
            && (item.AddressNumber == null || item.AddressNumber == "") && (item.StreetName == null || item.StreetName == "")
            && (item.PostCode == null || item.PostCode == "") && (item.Place == null || item.Place == "")
            && (item.Region == null || item.Region == "") && (item.Country == null || item.Country == "")
            && item.ReverseGeocodingProvider == null && item.ReverseGeocodingStorageMode == null
            && item.ReverseGeocodedAt == null, cancellationToken);

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
