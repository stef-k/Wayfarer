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
}

/// <summary>Owns the shared translatable current-authority candidate classification.</summary>
public sealed class LocationEnrichmentProgressQuery(ApplicationDbContext db) : ILocationEnrichmentProgressQuery
{
    /// <inheritdoc />
    public async Task<LocationEnrichmentProgressPresentation> ProjectAsync(string userId,
        PersonalProviderAuthorityBinding? authority, DateTime dbNow, CancellationToken cancellationToken = default)
    {
        if (authority is null) return new(0, 0, 0, false, null);
        var rows = Rows(userId, authority);
        var runnable = await rows.CountAsync(row => row.Attempt == null || (row.Attempt.OperationId == null
            && row.Attempt.Outcome != LocationEnrichmentOutcome.InvalidCoordinates
            && (!row.Current || (row.Attempt.Outcome != LocationEnrichmentOutcome.NoResult
                && row.Attempt.Outcome != LocationEnrichmentOutcome.AttemptLimit
                && row.Attempt.AdmittedAttemptCount < 3
                && (row.Attempt.NextAttemptAtUtc == null || row.Attempt.NextAttemptAtUtc <= dbNow)))), cancellationToken);
        var future = await rows.CountAsync(row => row.Attempt != null && row.Current
            && row.Attempt.OperationId == null && row.Attempt.Outcome == LocationEnrichmentOutcome.RetryableFailure
            && row.Attempt.AdmittedAttemptCount < 3 && row.Attempt.NextAttemptAtUtc > dbNow, cancellationToken);
        var permanent = await rows.CountAsync(row => row.Attempt != null && row.Attempt.OperationId == null
            && (row.Attempt.Outcome == LocationEnrichmentOutcome.InvalidCoordinates
                || (row.Current && (row.Attempt.Outcome == LocationEnrichmentOutcome.NoResult
                    || row.Attempt.Outcome == LocationEnrichmentOutcome.AttemptLimit
                    || row.Attempt.AdmittedAttemptCount >= 3))), cancellationToken);
        var next = await rows.Where(row => row.Attempt != null && row.Current
                && row.Attempt.OperationId == null && row.Attempt.Outcome == LocationEnrichmentOutcome.RetryableFailure
                && row.Attempt.AdmittedAttemptCount < 3 && row.Attempt.NextAttemptAtUtc > dbNow)
            .MinAsync(row => row.Attempt!.NextAttemptAtUtc, cancellationToken);
        var retryable = await rows.AnyAsync(row => row.Attempt != null && row.Current
            && row.Attempt.OperationId == null && (row.Attempt.Outcome == LocationEnrichmentOutcome.NoResult
                || row.Attempt.Outcome == LocationEnrichmentOutcome.AttemptLimit), cancellationToken);
        return new(runnable, future, permanent, retryable, next);
    }

    /// <inheritdoc />
    public Task<bool> HasRunnableAsync(string userId, PersonalProviderAuthorityBinding authority,
        DateTime dbNow, CancellationToken cancellationToken = default) => Rows(userId, authority).AnyAsync(row =>
            row.Attempt == null || (row.Attempt.OperationId == null
                && row.Attempt.Outcome != LocationEnrichmentOutcome.InvalidCoordinates
                && (!row.Current || (row.Attempt.Outcome != LocationEnrichmentOutcome.NoResult
                    && row.Attempt.Outcome != LocationEnrichmentOutcome.AttemptLimit
                    && row.Attempt.AdmittedAttemptCount < 3
                    && (row.Attempt.NextAttemptAtUtc == null || row.Attempt.NextAttemptAtUtc <= dbNow)))), cancellationToken);

    private IQueryable<CandidateRow> Rows(string userId, PersonalProviderAuthorityBinding authority) =>
        from location in WhollyUnenriched(db.Locations.Where(item => item.UserId == userId))
        join attempt in db.LocationEnrichmentAttempts.Where(item => item.UserId == userId)
            on location.Id equals attempt.LocationId into attempts
        from attempt in attempts.DefaultIfEmpty()
        select new CandidateRow
        {
            Attempt = attempt,
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

    /// <summary>Constrains every address, context, and provenance field used by enrichment.</summary>
    internal static IQueryable<Location> WhollyUnenriched(IQueryable<Location> query) => query.Where(value =>
        (value.Address == null || value.Address == "") && (value.FullAddress == null || value.FullAddress == "")
        && (value.AddressNumber == null || value.AddressNumber == "") && (value.StreetName == null || value.StreetName == "")
        && (value.PostCode == null || value.PostCode == "") && (value.Place == null || value.Place == "")
        && (value.Region == null || value.Region == "") && (value.Country == null || value.Country == "")
        && value.ReverseGeocodingProvider == null && value.ReverseGeocodingStorageMode == null
        && value.ReverseGeocodedAt == null);

    private sealed class CandidateRow
    {
        public LocationEnrichmentAttempt? Attempt { get; init; }
        public bool Current { get; init; }
    }
}
