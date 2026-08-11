using Microsoft.AspNetCore.Mvc;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>
    /// Creates a place in a normal owned region.
    /// </summary>
    [HttpPost("regions/{regionId:guid}/places")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreatePlace(Guid tripId, Guid regionId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _placeMutations.CreatePlaceAsync(tripId, regionId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
    }

    /// <summary>
    /// Updates or moves a place inside an owned trip.
    /// </summary>
    [HttpPut("places/{placeId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdatePlace(Guid tripId, Guid placeId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _placeMutations.UpdatePlaceAsync(tripId, placeId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
    }

    /// <summary>
    /// Deletes a place and endpoint segments that reference it.
    /// </summary>
    [HttpDelete("places/{placeId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeletePlace(Guid tripId, Guid placeId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _placeMutations.DeletePlaceAsync(
            tripId,
            placeId,
            userId!,
            Request.Headers["X-Wayfarer-Dependency-Confirmation"].FirstOrDefault(),
            cancellationToken);
        return ToActionResult(outcome);
    }

    /// <summary>
    /// Persists the complete desired place order for one normal owned region.
    /// </summary>
    [HttpPut("regions/{regionId:guid}/places/order")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OrderPlaces(Guid tripId, Guid regionId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _placeMutations.OrderPlacesAsync(tripId, regionId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
    }
}
