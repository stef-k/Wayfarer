using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;

namespace Wayfarer.Services.ExternalRouting;

/// <summary>Projects only safe per-Segment routing UX capability from the shared server authority.</summary>
public sealed class ExternalRoutingCapabilityProjector(AuthoritativeRoutingProviderResolver resolver)
{
    /// <summary>Projects every Segment for the authenticated user without exposing execution details.</summary>
    public async Task<IReadOnlyDictionary<Guid, EditorExternalRoutingCapabilityDto>> ProjectAsync(
        string userId, IReadOnlyList<Segment> segments, CancellationToken cancellationToken)
    {
        var results = new Dictionary<Guid, EditorExternalRoutingCapabilityDto>();
        foreach (var segment in segments)
        {
            var modes = ProviderDirectionsCatalog.For("geoapify");
            var resolution = await resolver.ResolveNativeAsync(userId, modes[0].Key, cancellationToken);
            if (resolution.Execution == null)
            {
                results[segment.Id] = Unavailable("Route suggestions are unavailable until Geoapify directions is verified and selected.");
                continue;
            }
            if (segment.FromPlace?.Location == null || segment.ToPlace?.Location == null
                || segment.Waypoints.Any(item => item.Place.Location == null) || segment.Waypoints.Count + 2 > 50)
            {
                results[segment.Id] = Unavailable("This Segment does not have a complete supported anchor sequence.");
                continue;
            }
            var execution = resolution.Execution;
            results[segment.Id] = new EditorExternalRoutingCapabilityDto(true, null, execution.DisplayName,
                null, execution.Disclosure, execution.Attribution, modes);
        }
        return results;
    }

    private static EditorExternalRoutingCapabilityDto Unavailable(string reason) =>
        new(false, reason, null, null, null, null);
}
