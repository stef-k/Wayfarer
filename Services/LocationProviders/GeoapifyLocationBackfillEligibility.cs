using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Owns database-translatable location shape and retry eligibility for Geoapify backfill.</summary>
public sealed partial class GeoapifyLocationBackfillService
{
    internal static IQueryable<int> CandidateQuery(ApplicationDbContext db, string userId,
        EnrichmentAuthority authority, DateTime now)
    {
        var attempts = db.LocationEnrichmentAttempts.Where(item => item.UserId == userId);
        var normal = from location in WhollyUnenriched(db.Locations.Where(item => item.UserId == userId))
            join attempt in attempts on location.Id equals attempt.LocationId into matches
            from attempt in matches.DefaultIfEmpty()
            where attempt == null || (attempt.OperationId == null
                && attempt.Outcome != LocationEnrichmentOutcome.InvalidCoordinates
                && ((attempt.ProviderKey != authority.ProviderKey
                    || attempt.ProviderProfileId != authority.ProfileId
                    || attempt.Capability != authority.Capability
                    || attempt.CredentialGeneration != authority.CredentialGeneration
                    || attempt.ConfigurationGeneration != authority.ConfigurationGeneration
                    || attempt.SelectionGeneration != authority.SelectionGeneration
                    || attempt.Verification != authority.Verification
                    || attempt.VerificationCredentialGeneration != authority.VerificationCredentialGeneration
                    || attempt.VerificationGeneration != authority.VerificationGeneration
                    || attempt.ConsentVersion != authority.ConsentVersion
                    || attempt.ConsentTimestamp != authority.ConsentTimestamp
                    || attempt.ConsentCredentialGeneration != authority.ConsentCredentialGeneration)
                    || (attempt.Outcome != LocationEnrichmentOutcome.NoResult
                        && attempt.Outcome != LocationEnrichmentOutcome.AttemptLimit
                        && attempt.AdmittedAttemptCount < 3
                        && (attempt.NextAttemptAtUtc == null || attempt.NextAttemptAtUtc <= now))))
            select location.Id;
        var repair = from location in IncompleteGeoapify(db.Locations.Where(item => item.UserId == userId))
            join attempt in attempts on location.Id equals attempt.LocationId
            where attempt.OperationId == null && attempt.ProviderKey == authority.ProviderKey
                && attempt.ProviderProfileId == authority.ProfileId && attempt.Capability == authority.Capability
                && attempt.CredentialGeneration == authority.CredentialGeneration
                && attempt.ConfigurationGeneration == authority.ConfigurationGeneration
                && attempt.SelectionGeneration == authority.SelectionGeneration
                && attempt.Verification == authority.Verification
                && attempt.VerificationCredentialGeneration == authority.VerificationCredentialGeneration
                && attempt.VerificationGeneration == authority.VerificationGeneration
                && attempt.ConsentVersion == authority.ConsentVersion
                && attempt.ConsentTimestamp == authority.ConsentTimestamp
                && attempt.ConsentCredentialGeneration == authority.ConsentCredentialGeneration
                && attempt.Outcome != LocationEnrichmentOutcome.InvalidCoordinates
                && attempt.Outcome != LocationEnrichmentOutcome.NoResult
                && attempt.Outcome != LocationEnrichmentOutcome.AttemptLimit
                && attempt.AdmittedAttemptCount < 3
                && (attempt.NextAttemptAtUtc == null || attempt.NextAttemptAtUtc <= now)
            select location.Id;
        var ids = normal.Concat(repair);
        return db.Locations.Where(item => item.UserId == userId && ids.Contains(item.Id))
            .OrderBy(item => item.Timestamp).ThenBy(item => item.Id).Select(item => item.Id);
    }

    private static bool MatchesAuthority(LocationEnrichmentAttempt attempt, EnrichmentAuthority authority) =>
        attempt.ProviderKey == authority.ProviderKey && attempt.ProviderProfileId == authority.ProfileId
        && attempt.Capability == authority.Capability
        && attempt.CredentialGeneration == authority.CredentialGeneration
        && attempt.ConfigurationGeneration == authority.ConfigurationGeneration
        && attempt.SelectionGeneration == authority.SelectionGeneration
        && attempt.Verification == authority.Verification
        && attempt.VerificationCredentialGeneration == authority.VerificationCredentialGeneration
        && attempt.VerificationGeneration == authority.VerificationGeneration
        && attempt.ConsentVersion == authority.ConsentVersion && attempt.ConsentTimestamp == authority.ConsentTimestamp
        && attempt.ConsentCredentialGeneration == authority.ConsentCredentialGeneration;

    private static bool IsRunnableShape(Location location, LocationEnrichmentAttempt? attempt,
        EnrichmentAuthority authority, DateTime now) => IsWhollyUnenriched(location)
        || (IsIncompleteGeoapify(location) && attempt is not null && MatchesAuthority(attempt, authority)
            && attempt.OperationId == null && attempt.Outcome is not (LocationEnrichmentOutcome.InvalidCoordinates
                or LocationEnrichmentOutcome.NoResult or LocationEnrichmentOutcome.AttemptLimit)
            && attempt.AdmittedAttemptCount < 3
            && (!attempt.NextAttemptAtUtc.HasValue || attempt.NextAttemptAtUtc <= now));

    private static IQueryable<LocationEnrichmentAttempt> FutureRetryQuery(ApplicationDbContext db,
        string userId, EnrichmentAuthority authority, DateTime now) =>
        from attempt in db.LocationEnrichmentAttempts
        join location in WhollyUnenriched(db.Locations.Where(item => item.UserId == userId))
            on attempt.LocationId equals location.Id
        where attempt.UserId == userId && attempt.ProviderKey == authority.ProviderKey
            && attempt.ProviderProfileId == authority.ProfileId && attempt.Capability == authority.Capability
            && attempt.CredentialGeneration == authority.CredentialGeneration
            && attempt.ConfigurationGeneration == authority.ConfigurationGeneration
            && attempt.SelectionGeneration == authority.SelectionGeneration
            && attempt.Verification == authority.Verification
            && attempt.VerificationCredentialGeneration == authority.VerificationCredentialGeneration
            && attempt.VerificationGeneration == authority.VerificationGeneration
            && attempt.ConsentVersion == authority.ConsentVersion
            && attempt.ConsentTimestamp == authority.ConsentTimestamp
            && attempt.ConsentCredentialGeneration == authority.ConsentCredentialGeneration
            && attempt.Outcome == LocationEnrichmentOutcome.RetryableFailure
            && attempt.AdmittedAttemptCount < 3 && attempt.NextAttemptAtUtc > now select attempt;

    public static bool IsWhollyUnenriched(Location value) => string.IsNullOrWhiteSpace(value.Address)
        && string.IsNullOrWhiteSpace(value.FullAddress) && string.IsNullOrWhiteSpace(value.AddressNumber)
        && string.IsNullOrWhiteSpace(value.StreetName) && string.IsNullOrWhiteSpace(value.PostCode)
        && string.IsNullOrWhiteSpace(value.Place) && string.IsNullOrWhiteSpace(value.Region)
        && string.IsNullOrWhiteSpace(value.Country) && value.ReverseGeocodingProvider == null
        && value.ReverseGeocodingStorageMode == null && value.ReverseGeocodedAt == null;

    private static bool IsIncompleteGeoapify(Location value) => string.IsNullOrWhiteSpace(value.Place)
        && value.ReverseGeocodingProvider == "geoapify"
        && value.ReverseGeocodingStorageMode == "persistent" && value.ReverseGeocodedAt.HasValue
        && (!string.IsNullOrWhiteSpace(value.Address) || !string.IsNullOrWhiteSpace(value.FullAddress)
            || !string.IsNullOrWhiteSpace(value.AddressNumber)
            || !string.IsNullOrWhiteSpace(value.StreetName) || !string.IsNullOrWhiteSpace(value.PostCode)
            || !string.IsNullOrWhiteSpace(value.Region) || !string.IsNullOrWhiteSpace(value.Country));

    private static IQueryable<Location> WhollyUnenriched(IQueryable<Location> query) => query.Where(value =>
        (value.Address == null || value.Address == "") && (value.FullAddress == null || value.FullAddress == "")
        && (value.AddressNumber == null || value.AddressNumber == "") && (value.StreetName == null || value.StreetName == "")
        && (value.PostCode == null || value.PostCode == "") && (value.Place == null || value.Place == "")
        && (value.Region == null || value.Region == "") && (value.Country == null || value.Country == "")
        && value.ReverseGeocodingProvider == null && value.ReverseGeocodingStorageMode == null
        && value.ReverseGeocodedAt == null);

    private static IQueryable<Location> IncompleteGeoapify(IQueryable<Location> query) => query.Where(value =>
        (value.Place == null || value.Place.Trim() == "") && value.ReverseGeocodingProvider == "geoapify"
        && value.ReverseGeocodingStorageMode == "persistent" && value.ReverseGeocodedAt != null
        && ((value.Address != null && value.Address.Trim() != "")
            || (value.FullAddress != null && value.FullAddress.Trim() != "")
            || (value.AddressNumber != null && value.AddressNumber.Trim() != "")
            || (value.StreetName != null && value.StreetName.Trim() != "")
            || (value.PostCode != null && value.PostCode.Trim() != "")
            || (value.Region != null && value.Region.Trim() != "")
            || (value.Country != null && value.Country.Trim() != "")));
}
