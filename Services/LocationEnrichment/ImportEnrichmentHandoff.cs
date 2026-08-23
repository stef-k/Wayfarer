using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Turns committed import opt-in into the user's single durable workflow intent.</summary>
public interface IImportEnrichmentHandoff
{
    Task EnsureAsync(string userId, CancellationToken cancellationToken = default);
    Task<EnrichmentCommandResult> StartAsync(string userId, CancellationToken cancellationToken = default);
    Task<EnrichmentCommandResult> RetryDeferredAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>Creates or resumes relational intent before projecting one Quartz trigger.</summary>
public sealed class ImportEnrichmentHandoff(
    ApplicationDbContext db, IWorkflowScheduleProjection projection) : IImportEnrichmentHandoff
{
    public async Task EnsureAsync(string userId, CancellationToken cancellationToken = default)
        => _ = await StartAsync(userId, cancellationToken);

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
        await db.SaveChangesAsync(cancellationToken);
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
        await db.SaveChangesAsync(cancellationToken);
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
}

/// <summary>Returns bounded command feedback without provider or scheduler internals.</summary>
public sealed record EnrichmentCommandResult(bool Succeeded, string Code)
{
    public static EnrichmentCommandResult Success(string code) => new(true, code);
    public static EnrichmentCommandResult Conflict(string code) => new(false, code);
}
