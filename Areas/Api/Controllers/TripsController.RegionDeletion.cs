using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Bearer-only authoritative preview for future Mobile; an Identity cookie grants no authority.</summary>
    [HttpPost("{tripId:guid}/regions/{regionId:guid}/deletion-preview")]
    public Task<IActionResult> PreviewRegionDeletion(Guid tripId, Guid regionId, CancellationToken token) =>
        RegionDeletionAsync(tripId, regionId, false, token);

    /// <summary>Bearer-only confirmed operation using the same lifecycle and editor envelope as the cookie adapter.</summary>
    [HttpPost("{tripId:guid}/regions/{regionId:guid}/deletion")]
    public Task<IActionResult> ConfirmRegionDeletion(Guid tripId, Guid regionId, CancellationToken token) =>
        RegionDeletionAsync(tripId, regionId, true, token);

    private async Task<IActionResult> RegionDeletionAsync(Guid tripId, Guid regionId, bool mutation, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, token);
        return user == null ? Unauthorized()
            : await RegionDeletionEndpoint.ExecuteAsync(this, _dbContext, _lifecycle, tripId, regionId, user.Id, mutation, token,
                ct => authority.RevalidateMutationAsync(HttpContext, user.Id, ct));
    }
}
