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
        if (authority is null) return new(0, 0, 0, cannotRetry, null, incomplete);
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
        var manualRetry = await ManuallyRetryableAttempts(db, userId, authority).CountAsync(cancellationToken);
        var next = await rows.Where(row => row.Attempt != null && row.Current
                && row.Attempt.OperationId == null && row.Attempt.Outcome == LocationEnrichmentOutcome.RetryableFailure
                && row.Attempt.AdmittedAttemptCount < 3 && row.Attempt.NextAttemptAtUtc > dbNow)
            .MinAsync(row => row.Attempt!.NextAttemptAtUtc, cancellationToken);
        return new(runnable, future, manualRetry, cannotRetry, next, incomplete);
    }

    /// <inheritdoc />
    public Task<int> CountIncompleteGeoapifyAsync(string userId,
        CancellationToken cancellationToken = default) => IncompleteGeoapifyLocations(db, userId)
        .CountAsync(cancellationToken);

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
