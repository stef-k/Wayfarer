using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Returns scalar enrichment eligibility facts without materializing Locations or attempts.</summary>
public interface ILocationEnrichmentProgressQuery
{
    Task<LocationEnrichmentProgressPresentation> ProjectAsync(string userId,
        PersonalProviderAuthorityBinding? authority, DateTime dbNow, CancellationToken cancellationToken = default);
    Task<bool> HasRunnableAsync(string userId, PersonalProviderAuthorityBinding authority,
        DateTime dbNow, CancellationToken cancellationToken = default);
    Task<int> CountIncompleteGeoapifyAsync(string userId, CancellationToken cancellationToken = default);
}

/// <summary>Owns the shared translatable current-authority candidate classification.</summary>
public sealed class LocationEnrichmentProgressQuery(ApplicationDbContext db) : ILocationEnrichmentProgressQuery
{
    /// <inheritdoc />
    public async Task<LocationEnrichmentProgressPresentation> ProjectAsync(string userId,
        PersonalProviderAuthorityBinding? authority, DateTime dbNow, CancellationToken cancellationToken = default)
    {
        var cannotRetry = await CannotRetryAttempts(db, userId).CountAsync(cancellationToken);
        var incomplete = await CountIncompleteGeoapifyAsync(userId, cancellationToken);
        // A claim is in flight only while its complete workflow owner remains live; otherwise it awaits recovery.
        var operations = await db.LocationEnrichmentAttempts.Where(item => item.UserId == userId && item.OperationId != null)
            .Select(item => item.Workflow != null && item.Workflow.IntentEnabled
                && item.Workflow.State == LocationEnrichmentState.Running
                && item.OperationWorkflowEpoch == item.Workflow.Epoch
                && item.OperationLeaseId == item.Workflow.ExecutionLeaseId
                && item.OperationFencingGeneration == item.Workflow.ExecutionFencingGeneration
                && item.Workflow.ExecutionLeaseExpiresAtUtc > dbNow)
            .GroupBy(owned => 1).Select(group => new { Total = group.Count(), Owned = group.Count(owned => owned) })
            .SingleOrDefaultAsync(cancellationToken);
        var inFlight = operations?.Owned ?? 0;
        var recovery = (operations?.Total ?? 0) - inFlight;
        // Only an admitted terminal repair supports a provider-no-locality explanation.
        var noLocality = await (from location in IncompleteGeoapifyLocations(db, userId)
            join attempt in db.LocationEnrichmentAttempts.Where(item => item.UserId == userId)
                on location.Id equals attempt.LocationId
            where attempt.OperationId == null && attempt.AdmittedAttemptCount > 0
                && attempt.Outcome == LocationEnrichmentOutcome.NoResult
            select attempt.Id).CountAsync(cancellationToken);
        if (authority is null) return new(0, 0, 0, cannotRetry, null, incomplete, inFlight, recovery, noLocality);
        var rows = Rows(userId, authority);
        var runnable = await rows.CountAsync(row => (row.Normal || row.Current) && (row.Attempt == null || (row.Attempt.OperationId == null
            && row.Attempt.Outcome != LocationEnrichmentOutcome.InvalidCoordinates
            && (!row.Current || (row.Attempt.Outcome != LocationEnrichmentOutcome.NoResult
                && row.Attempt.Outcome != LocationEnrichmentOutcome.AttemptLimit
                && row.Attempt.AdmittedAttemptCount < 3
                && (row.Attempt.NextAttemptAtUtc == null || row.Attempt.NextAttemptAtUtc <= dbNow))))), cancellationToken);
        var future = await rows.CountAsync(row => row.Attempt != null && row.Current
            && row.Attempt.OperationId == null && row.Attempt.Outcome == LocationEnrichmentOutcome.RetryableFailure
            && row.Attempt.AdmittedAttemptCount < 3 && row.Attempt.NextAttemptAtUtc > dbNow, cancellationToken);
        var manualRetry = await ManuallyRetryableAttempts(db, userId, authority).CountAsync(cancellationToken);
        var next = await rows.Where(row => row.Attempt != null && row.Current
                && row.Attempt.OperationId == null && row.Attempt.Outcome == LocationEnrichmentOutcome.RetryableFailure
                && row.Attempt.AdmittedAttemptCount < 3 && row.Attempt.NextAttemptAtUtc > dbNow)
            .MinAsync(row => row.Attempt!.NextAttemptAtUtc, cancellationToken);
        return new(runnable, future, manualRetry, cannotRetry, next, incomplete, inFlight, recovery, noLocality);
    }

    /// <inheritdoc />
    public Task<int> CountIncompleteGeoapifyAsync(string userId,
        CancellationToken cancellationToken = default) => IncompleteGeoapifyLocations(db, userId)
        .CountAsync(cancellationToken);

    /// <inheritdoc />
    public Task<bool> HasRunnableAsync(string userId, PersonalProviderAuthorityBinding authority,
        DateTime dbNow, CancellationToken cancellationToken = default) => Rows(userId, authority).AnyAsync(row =>
            (row.Normal || row.Current) && (row.Attempt == null || (row.Attempt.OperationId == null
                && row.Attempt.Outcome != LocationEnrichmentOutcome.InvalidCoordinates
                && (!row.Current || (row.Attempt.Outcome != LocationEnrichmentOutcome.NoResult
                    && row.Attempt.Outcome != LocationEnrichmentOutcome.AttemptLimit
                    && row.Attempt.AdmittedAttemptCount < 3
                    && (row.Attempt.NextAttemptAtUtc == null || row.Attempt.NextAttemptAtUtc <= dbNow))))), cancellationToken);

    /// <summary>Normal work may reset stale authority; partial repairs require affirmative current authority.</summary>
    private IQueryable<CandidateRow> Rows(string userId, PersonalProviderAuthorityBinding authority) =>
        from location in db.Locations.Where(item => item.UserId == userId
            && (WhollyUnenriched(db.Locations).Any(normal => normal.Id == item.Id)
                || IncompleteGeoapifyLocations(db, userId).Any(partial => partial.Id == item.Id)))
        join attempt in db.LocationEnrichmentAttempts.Where(item => item.UserId == userId)
            on location.Id equals attempt.LocationId into attempts
        from attempt in attempts.DefaultIfEmpty()
        select new CandidateRow
        {
            Attempt = attempt,
            Normal = WhollyUnenriched(db.Locations).Any(normal => normal.Id == location.Id),
            Current = attempt != null && attempt.ProviderKey == authority.ProviderKey
                && attempt.ProviderProfileId == authority.ProfileId
                && attempt.Capability == PersonalProviderCapability.Geocoding
                && attempt.CredentialGeneration == authority.CredentialGeneration
                && attempt.ConfigurationGeneration == authority.CapabilityGeneration
                && attempt.SelectionGeneration == authority.SelectionGeneration
                && attempt.Verification == authority.Verification
                && attempt.VerificationCredentialGeneration == authority.VerifiedCredentialGeneration
                && attempt.VerificationGeneration == authority.VerifiedCapabilityGeneration
                && attempt.ConsentVersion == authority.ConsentVersion
                && attempt.ConsentTimestamp == authority.ConsentedAt
                && attempt.ConsentCredentialGeneration == authority.ConsentCredentialGeneration
        };

    /// <summary>Owns the exact current-authority rows offered and reset by explicit manual retry.</summary>
    internal static IQueryable<LocationEnrichmentAttempt> ManuallyRetryableAttempts(
        ApplicationDbContext context, string userId, PersonalProviderAuthorityBinding authority) =>
        from attempt in context.LocationEnrichmentAttempts
        join location in WhollyUnenriched(context.Locations.Where(item => item.UserId == userId))
            on new { attempt.UserId, Id = attempt.LocationId } equals new { location.UserId, location.Id }
        where attempt.UserId == userId && attempt.OperationId == null
            && (attempt.Outcome == LocationEnrichmentOutcome.NoResult
                || attempt.Outcome == LocationEnrichmentOutcome.AttemptLimit)
            && attempt.ProviderKey == authority.ProviderKey && attempt.ProviderProfileId == authority.ProfileId
            && attempt.Capability == PersonalProviderCapability.Geocoding
            && attempt.CredentialGeneration == authority.CredentialGeneration
            && attempt.ConfigurationGeneration == authority.CapabilityGeneration
            && attempt.SelectionGeneration == authority.SelectionGeneration
            && attempt.Verification == authority.Verification
            && attempt.VerificationCredentialGeneration == authority.VerifiedCredentialGeneration
            && attempt.VerificationGeneration == authority.VerifiedCapabilityGeneration
            && attempt.ConsentVersion == authority.ConsentVersion && attempt.ConsentTimestamp == authority.ConsentedAt
            && attempt.ConsentCredentialGeneration == authority.ConsentCredentialGeneration
        select attempt;

    /// <summary>Returns provider-derived partial records without admitting or scheduling provider contact.</summary>
    internal static IQueryable<Location> IncompleteGeoapifyLocations(ApplicationDbContext context, string userId) =>
        context.Locations.Where(value => value.UserId == userId
            && (value.Place == null || value.Place.Trim() == "")
            && value.ReverseGeocodingProvider == "geoapify"
            && value.ReverseGeocodingStorageMode == "persistent"
            && value.ReverseGeocodedAt != null
            && ((value.Address != null && value.Address.Trim() != "")
            || (value.FullAddress != null && value.FullAddress.Trim() != "")
            || (value.ProviderAddressLine1 != null && value.ProviderAddressLine1.Trim() != "")
            || (value.AddressNumber != null && value.AddressNumber.Trim() != "")
                || (value.StreetName != null && value.StreetName.Trim() != "")
                || (value.PostCode != null && value.PostCode.Trim() != "")
                || (value.Region != null && value.Region.Trim() != "")
                || (value.Country != null && value.Country.Trim() != "")));

    /// <summary>Returns invalid-coordinate rows independently of current provider authority.</summary>
    internal static IQueryable<LocationEnrichmentAttempt> CannotRetryAttempts(
        ApplicationDbContext context, string userId) =>
        from attempt in context.LocationEnrichmentAttempts
        join location in WhollyUnenriched(context.Locations.Where(item => item.UserId == userId))
            on new { attempt.UserId, Id = attempt.LocationId } equals new { location.UserId, location.Id }
        where attempt.UserId == userId && attempt.OperationId == null
            && attempt.Outcome == LocationEnrichmentOutcome.InvalidCoordinates
        select attempt;

    /// <summary>Constrains every address, context, and provenance field used by enrichment.</summary>
    internal static IQueryable<Location> WhollyUnenriched(IQueryable<Location> query) => query.Where(value =>
        (value.Address == null || value.Address == "") && (value.FullAddress == null || value.FullAddress == "")
        && (value.ProviderAddressLine1 == null || value.ProviderAddressLine1 == "")
        && (value.AddressNumber == null || value.AddressNumber == "") && (value.StreetName == null || value.StreetName == "")
        && (value.PostCode == null || value.PostCode == "") && (value.Place == null || value.Place == "")
        && (value.Region == null || value.Region == "") && (value.Country == null || value.Country == "")
        && value.ReverseGeocodingProvider == null && value.ReverseGeocodingStorageMode == null
        && value.ReverseGeocodedAt == null);

    private sealed class CandidateRow
    {
        public LocationEnrichmentAttempt? Attempt { get; init; }
        public bool Current { get; init; }
        public bool Normal { get; init; }
    }
}
