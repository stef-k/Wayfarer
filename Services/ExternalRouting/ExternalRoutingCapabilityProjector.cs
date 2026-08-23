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
            if (segment.TransportProfileId is not { } profileId)
            {
                results[segment.Id] = Unavailable("The selected transport profile is unavailable.");
                continue;
            }
            var resolution = await resolver.ResolveAsync(userId, profileId, cancellationToken);
            if (resolution.Execution == null)
            {
                results[segment.Id] = Unavailable(resolution.ErrorCode switch
                {
                    "unmapped-transport-profile" => "Route suggestions are not configured for this transport profile.",
                    "unsupported-transport-profile" => "This routing provider does not support the mapped transport mode.",
                    _ => "Route suggestions are temporarily unavailable."
                });
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
                execution.Profile, execution.Disclosure, execution.Attribution);
        }
        return results;
    }

    private static EditorExternalRoutingCapabilityDto Unavailable(string reason) =>
        new(false, reason, null, null, null, null);
}
