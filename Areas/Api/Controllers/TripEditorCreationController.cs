using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>
    /// Creates a normal region for an owned trip.
    /// </summary>
    [HttpPost("regions/conditional")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateRegion(Guid tripId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        try
        {
            var outcome = await _regionMutations.CreateRegionAsync(tripId, userId!, Request.Body, cancellationToken);
            return ToActionResult(outcome);
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }

    /// <summary>
    /// Creates a place in a normal owned region.
    /// </summary>
    [HttpPost("regions/{regionId:guid}/places/conditional")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePlace(Guid tripId, Guid regionId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        try
        {
            var outcome = await _placeMutations.CreatePlaceAsync(tripId, regionId, userId!, Request.Body, cancellationToken);
            return ToActionResult(outcome);
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }

}
