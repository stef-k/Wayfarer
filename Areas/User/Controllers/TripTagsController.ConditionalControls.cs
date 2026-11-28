using Microsoft.AspNetCore.Mvc;
using Wayfarer.Areas.Api.Controllers;

namespace Wayfarer.Areas.User.Controllers;

public sealed partial class TripTagsController
{
    /// <summary>Executes a frozen Trip control submission.</summary>
    [HttpPost("attach/conditional")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> ExecuteAttachTags(Guid tripId, CancellationToken token) =>
        WithTripControlAuthorityAsync(routes => routes.ExecuteAsync(tripId, token), "tags-attach");

    /// <summary>Issues a permit bound to this Trip control.</summary>
    [HttpPost("attach/conditional/operations")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> IssueAttachTags(Guid tripId, CancellationToken token) =>
        WithTripControlAuthorityAsync(routes => routes.IssueAsync(tripId, token), "tags-attach");

    /// <summary>Reads historical evidence for this Trip control.</summary>
    [HttpGet("attach/conditional/operations/{operationId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> LookupAttachTags(Guid tripId, Guid operationId, CancellationToken token) =>
        WithTripControlAuthorityAsync(routes => routes.LookupAsync(tripId, operationId, token), "tags-attach");

    /// <summary>Executes a frozen Trip control submission.</summary>
    [HttpPost("detach/conditional")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> ExecuteDetachTags(Guid tripId, CancellationToken token) =>
        WithTripControlAuthorityAsync(routes => routes.ExecuteAsync(tripId, token), "tags-detach");

    /// <summary>Issues a permit bound to this Trip control.</summary>
    [HttpPost("detach/conditional/operations")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> IssueDetachTags(Guid tripId, CancellationToken token) =>
        WithTripControlAuthorityAsync(routes => routes.IssueAsync(tripId, token), "tags-detach");

    /// <summary>Reads historical evidence for this Trip control.</summary>
    [HttpGet("detach/conditional/operations/{operationId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> LookupDetachTags(Guid tripId, Guid operationId, CancellationToken token) =>
        WithTripControlAuthorityAsync(routes => routes.LookupAsync(tripId, operationId, token), "tags-detach");

    /// <summary>Uses the active User-cookie credential for this receipt route.</summary>
    private Task<IActionResult> WithTripControlAuthorityAsync(Func<TripMetadataReceiptRoutes, Task<IActionResult>> dispatch, string kind) =>
        TripMetadataReceiptRoutes.WithWebAuthorityAsync(HttpContext, _dbContext, kind, dispatch);
}
