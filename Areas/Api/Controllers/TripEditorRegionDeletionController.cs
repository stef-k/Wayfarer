using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>Returns current consequences without mutating the graph; cookie POST requires antiforgery.</summary>
    [HttpPost("regions/{regionId:guid}/deletion-preview")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> PreviewRegionDeletion(Guid tripId, Guid regionId,
        [FromServices] PlaceRegionLifecycleService lifecycle, CancellationToken token) =>
        RegionDeletionAsync(tripId, regionId, lifecycle, false, token);

    /// <summary>Dispatches one explicitly confirmed disposition operation; never retries a destructive request.</summary>
    [HttpPost("regions/{regionId:guid}/deletion")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ConfirmRegionDeletion(Guid tripId, Guid regionId,
        [FromServices] PlaceRegionLifecycleService lifecycle, CancellationToken token) =>
        RegionDeletionAsync(tripId, regionId, lifecycle, true, token);

    private Task<IActionResult> RegionDeletionAsync(Guid tripId, Guid regionId, PlaceRegionLifecycleService lifecycle,
        bool mutation, CancellationToken token)
    {
        var failure = RequireEditorUser(out var userId);
        return failure != null ? Task.FromResult(failure)
            : RegionDeletionEndpoint.ExecuteAsync(this, _dbContext, lifecycle, tripId, regionId, userId!, mutation, token);
    }
}
