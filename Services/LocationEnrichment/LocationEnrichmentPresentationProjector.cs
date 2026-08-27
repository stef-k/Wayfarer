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
    ApplicationDbContext db, IPersonalProviderStatusReader statusReader,
    ILocationEnrichmentProgressQuery progressQuery) : ILocationEnrichmentPresentationProjector
{
    /// <inheritdoc />
    public async Task<LocationEnrichmentPresentationModel> ProjectAsync(
        string userId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        var workflow = await db.LocationEnrichmentWorkflows.AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        var inspection = await statusReader.InspectPersistentGeocodingAsync(userId, cancellationToken);
        var progress = await progressQuery.ProjectAsync(
            userId, inspection.Binding, inspection.DatabaseNowUtc, cancellationToken);
        return LocationEnrichmentPresentation.Build(workflow, ProjectAuthority(inspection), progress);
    }

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
