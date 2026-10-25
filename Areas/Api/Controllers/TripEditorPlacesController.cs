using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
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

        try { return ToActionResult(await _placeMutations.UpdatePlaceAsync(tripId, placeId, userId!, Request.Body, cancellationToken)); }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }

    /// <summary>
    /// Rejects unconditional Place deletion; use the content preview and confirmed POST.
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

        if (!await _dbContext.Places.AsNoTracking().AnyAsync(p => p.Id == placeId && p.UserId == userId && p.Region.TripId == tripId && p.Region.UserId == userId && p.Region.Trip.UserId == userId, cancellationToken)) return NotFound();
        return new TripMetadataMutationRejected(409, "mutation-confirmation-required",
            "Use the content-bound deletion preview and confirmed POST contract.").ToActionResult();
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

        try { return ToActionResult(await _placeMutations.OrderPlacesAsync(tripId, regionId, userId!, Request.Body, cancellationToken)); }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}
