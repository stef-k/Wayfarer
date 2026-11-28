using Microsoft.AspNetCore.Mvc;
using Wayfarer.Areas.Api.Controllers;

namespace Wayfarer.Areas.User.Controllers;

public partial class TripController
{
    /// <summary>Executes a frozen Trip control submission.</summary>
    [HttpPost("/User/Trip/{tripId:guid}/share-progress/conditional")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> ExecuteViewerSharing(Guid tripId, CancellationToken token) =>
        WithTripControlAuthorityAsync(routes => routes.ExecuteAsync(tripId, token), "viewer-sharing");

    /// <summary>Issues a permit bound to this Trip control.</summary>
    [HttpPost("/User/Trip/{tripId:guid}/share-progress/conditional/operations")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> IssueViewerSharing(Guid tripId, CancellationToken token) =>
        WithTripControlAuthorityAsync(routes => routes.IssueAsync(tripId, token), "viewer-sharing");

    /// <summary>Reads historical evidence for this Trip control.</summary>
    [HttpGet("/User/Trip/{tripId:guid}/share-progress/conditional/operations/{operationId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> LookupViewerSharing(Guid tripId, Guid operationId, CancellationToken token) =>
        WithTripControlAuthorityAsync(routes => routes.LookupAsync(tripId, operationId, token), "viewer-sharing");

    /// <summary>Uses the active User-cookie credential for this receipt route.</summary>
    private Task<IActionResult> WithTripControlAuthorityAsync(Func<TripMetadataReceiptRoutes, Task<IActionResult>> dispatch, string kind) =>
        TripMetadataReceiptRoutes.WithWebAuthorityAsync(HttpContext, _dbContext, kind, dispatch);
}
