using Wayfarer.Models;
using Microsoft.AspNetCore.Http;

namespace Wayfarer.Models.Dtos.TripViewer;

/// <summary>
/// Maps loaded trip entities into the read-only Trip Viewer state contract.
/// </summary>
public static class TripViewerStateMapper
{
    /// <summary>Builds private owner viewer state for an already ownership-filtered trip.</summary>
    public static TripViewerStateDto ToPrivateState(
        Trip trip,
        IReadOnlyList<PlaceVisitEvent> visitEvents,
        IQueryCollection query) =>
        throw new NotImplementedException("Trip viewer private state mapping is not implemented yet.");

    /// <summary>Builds public or embed viewer state for an already public-filtered trip.</summary>
    public static TripViewerStateDto ToPublicState(
        Trip trip,
        IReadOnlyList<PlaceVisitEvent> visitEvents,
        bool isOwner,
        bool isAuthenticated,
        bool embed,
        IQueryCollection query) =>
        throw new NotImplementedException("Trip viewer public state mapping is not implemented yet.");
}
