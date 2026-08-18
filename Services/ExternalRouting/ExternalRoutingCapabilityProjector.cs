using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Projects only safe per-Segment routing UX capability from server authority.</summary>
public sealed class ExternalRoutingCapabilityProjector
{
    private readonly ApplicationDbContext _dbContext;

    /// <summary>Initializes safe capability projection.</summary>
    public ExternalRoutingCapabilityProjector(ApplicationDbContext dbContext) => _dbContext = dbContext;

    /// <summary>Projects every Segment independently without endpoint, credential, catalog, or probe data.</summary>
    public async Task<IReadOnlyDictionary<Guid, EditorExternalRoutingCapabilityDto>> ProjectAsync(
        IReadOnlyList<Segment> segments, CancellationToken cancellationToken)
    {
        var settings = await _dbContext.ApplicationSettings.AsNoTracking()
            .Include(item => item.ActiveRoutingProviderConfiguration)!.ThenInclude(item => item!.ProfileMappings)
            .SingleAsync(item => item.Id == 1, cancellationToken);
        var provider = settings.ActiveRoutingProviderConfiguration;
        var profileLabels = await _dbContext.Set<TransportProfile>().AsNoTracking()
            .ToDictionaryAsync(item => item.Id, item => item.Label, cancellationToken);
        return segments.ToDictionary(segment => segment.Id, segment => Project(segment, settings, provider, profileLabels));
    }

    private static EditorExternalRoutingCapabilityDto Project(
        Segment segment, ApplicationSettings settings, RoutingProviderConfiguration? provider,
        IReadOnlyDictionary<Guid, string> profileLabels)
    {
        if (!settings.ExternalRouteGenerationEnabled) return Unavailable("External route generation is disabled.");
        if (provider is not { Enabled: true } || provider.VerifiedConfigurationVersion != provider.ConfigurationVersion)
            return Unavailable("External route generation is temporarily unavailable.");
        if (segment.TransportProfileId is not { } profileId
            || provider.ProfileMappings.SingleOrDefault(item => item.TransportProfileId == profileId) is not { } mapping)
            return Unavailable("The selected transport profile is not supported by the active routing provider.");
        if (segment.FromPlace?.Location == null || segment.ToPlace?.Location == null
            || segment.Waypoints.Any(item => item.Place.Location == null) || segment.Waypoints.Count + 2 > 50)
            return Unavailable("This Segment does not have a complete supported anchor sequence.");
        return new EditorExternalRoutingCapabilityDto(true, null, provider.DisplayName,
            profileLabels.GetValueOrDefault(profileId) ?? mapping.OsrmProfile,
            provider.ExternalCoordinateDisclosure, provider.Attribution);
    }

    private static EditorExternalRoutingCapabilityDto Unavailable(string reason) =>
        new(false, reason, null, null, null, null);
}
