using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Services.LocationEnrichment;

/// <summary>Projects one authenticated user's current relational enrichment presentation.</summary>
public interface ILocationEnrichmentPresentationProjector
{
    Task<LocationEnrichmentPresentationModel> ProjectAsync(
        string userId, CancellationToken cancellationToken = default);
}

/// <summary>Translates contact-gate authority and durable work rows into content-safe display facts.</summary>
public sealed class LocationEnrichmentPresentationProjector(
    ApplicationDbContext db, IPersonalProviderInspection contactGate) : ILocationEnrichmentPresentationProjector
{
    /// <inheritdoc />
    public async Task<LocationEnrichmentPresentationModel> ProjectAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var workflow = await db.LocationEnrichmentWorkflows.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var inspection = await contactGate.InspectPersistentGeocodingAsync(userId, cancellationToken);
        var now = DateTime.UtcNow;
        var progress = await ProjectProgressAsync(userId, inspection.Binding, now, cancellationToken);
        return LocationEnrichmentPresentation.Build(workflow, ProjectAuthority(inspection), progress);
    }

    private async Task<LocationEnrichmentProgressPresentation> ProjectProgressAsync(string userId,
        PersonalProviderAuthorityBinding? authority, DateTime now, CancellationToken cancellationToken)
    {
        var locationIds = await db.Locations.AsNoTracking().Where(item => item.UserId == userId
            && (item.Address == null || item.Address == "") && (item.FullAddress == null || item.FullAddress == "")
            && (item.AddressNumber == null || item.AddressNumber == "") && (item.StreetName == null || item.StreetName == "")
            && (item.PostCode == null || item.PostCode == "") && (item.Place == null || item.Place == "")
            && (item.Region == null || item.Region == "") && (item.Country == null || item.Country == "")
            && item.ReverseGeocodingProvider == null && item.ReverseGeocodingStorageMode == null
            && item.ReverseGeocodedAt == null).Select(item => item.Id).ToListAsync(cancellationToken);
        if (authority is null)
            return new(0, 0, 0, false, null);

        var attempts = await db.LocationEnrichmentAttempts.AsNoTracking()
            .Where(item => item.UserId == userId && locationIds.Contains(item.LocationId))
            .ToDictionaryAsync(item => item.LocationId, cancellationToken);
        var runnable = 0;
        var future = 0;
        var permanent = 0;
        DateTime? next = null;
        foreach (var locationId in locationIds)
        {
            if (!attempts.TryGetValue(locationId, out var attempt) || !IsCurrent(attempt, authority))
            {
                runnable++;
                continue;
            }
            if (attempt.OperationId.HasValue) continue;
            if (attempt.Outcome is LocationEnrichmentOutcome.InvalidCoordinates
                or LocationEnrichmentOutcome.NoResult or LocationEnrichmentOutcome.AttemptLimit)
            {
                permanent++;
                continue;
            }
            if (attempt.AdmittedAttemptCount < 3 && attempt.NextAttemptAtUtc > now)
            {
                future++;
                next = !next.HasValue || attempt.NextAttemptAtUtc < next ? attempt.NextAttemptAtUtc : next;
                continue;
            }
            if (attempt.AdmittedAttemptCount < 3) runnable++;
            else permanent++;
        }
        return new(runnable, future, permanent, permanent > 0, next);
    }

    private static bool IsCurrent(LocationEnrichmentAttempt attempt, PersonalProviderAuthorityBinding authority) =>
        attempt.ProviderKey == authority.ProviderKey && attempt.ProviderProfileId == authority.ProfileId
        && attempt.Capability == PersonalProviderCapability.Geocoding
        && attempt.CredentialGeneration == authority.CredentialGeneration
        && attempt.ConfigurationGeneration == authority.CapabilityGeneration
        && attempt.SelectionGeneration == authority.SelectionGeneration
        && attempt.Verification == authority.Verification
        && attempt.VerificationCredentialGeneration == authority.VerifiedCredentialGeneration
        && attempt.VerificationGeneration == authority.VerifiedCapabilityGeneration
        && attempt.ConsentVersion == authority.ConsentVersion
        && attempt.ConsentTimestamp == authority.ConsentedAt
        && attempt.ConsentCredentialGeneration == authority.ConsentCredentialGeneration;

    private static LocationEnrichmentAuthorityPresentation ProjectAuthority(PersonalProviderInspection inspection)
    {
        var providerName = inspection.ProviderKey switch
        { "geoapify" => "Geoapify", "mapbox" => "Mapbox Permanent", _ => "Not selected" };
        var summary = inspection.Exhausted ? "The Wayfarer provider budget is exhausted."
            : inspection.Category switch
            {
                PersonalProviderAdmissionCategory.Admitted => "Provider authority is current.",
                PersonalProviderAdmissionCategory.NoProviderSelected => "No geocoding provider is selected.",
                PersonalProviderAdmissionCategory.Unauthorized => "Provider access is not authorized or was revoked.",
                PersonalProviderAdmissionCategory.Unverified => "Provider verification is required or stale.",
                PersonalProviderAdmissionCategory.ConsentRequired => "Current Mapbox Permanent consent is required.",
                PersonalProviderAdmissionCategory.CredentialUnavailable => "The protected provider credential is unavailable.",
                _ => "The selected provider is unavailable."
            };
        var usage = inspection.Usage;
        var window = inspection.ProviderKey switch
        {
            "geoapify" => "Wayfarer rolling 24-hour shared geocoding and routing window",
            "mapbox" => "Wayfarer UTC calendar-month Permanent Geocoding cycle",
            _ => "No active usage window"
        };
        return new(inspection.ProviderKey, providerName, inspection.Available, summary,
            inspection.GuardEnabled, usage?.Used ?? 0, usage?.Limit ?? 0, usage?.Unit ?? "credits",
            window, inspection.NextAvailableAt?.UtcDateTime);
    }
}
