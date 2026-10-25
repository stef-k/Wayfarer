using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Read-only bearer coordinate preparation with retained original parent/location evidence.</summary>
    [HttpPost("{tripId:guid}/conditional/places/{placeId:guid}/coordinates/prepare")]
    public Task<IActionResult> PreparePlaceCoordinates(Guid tripId, Guid placeId, CancellationToken token) => PlaceStructureAsync(tripId, placeId, "coordinates", true, token);

    /// <summary>Atomic bearer coordinates, route effects and D3 enrichment invalidation; Address remains untouched.</summary>
    [HttpPost("{tripId:guid}/conditional/places/{placeId:guid}/coordinates")]
    public Task<IActionResult> ApplyPlaceCoordinates(Guid tripId, Guid placeId, CancellationToken token) => PlaceStructureAsync(tripId, placeId, "coordinates", false, token);

    /// <summary>Read-only same-Trip move preparation, binding target material and both exact original membership/order sets.</summary>
    [HttpPost("{tripId:guid}/conditional/places/{placeId:guid}/move/prepare")]
    public Task<IActionResult> PreparePlaceMove(Guid tripId, Guid placeId, CancellationToken token) => PlaceStructureAsync(tripId, placeId, "move", true, token);

    /// <summary>Transfers exactly one Place and atomically writes the complete two-Region vector.</summary>
    [HttpPost("{tripId:guid}/conditional/places/{placeId:guid}/move")]
    public Task<IActionResult> ApplyPlaceMove(Guid tripId, Guid placeId, CancellationToken token) => PlaceStructureAsync(tripId, placeId, "move", false, token);

    /// <summary>Read-only complete scoped ordering preparation, including bearer Unassigned capability.</summary>
    [HttpPost("{tripId:guid}/conditional/regions/{regionId:guid}/places/order/prepare")]
    public Task<IActionResult> PreparePlaceOrder(Guid tripId, Guid regionId, CancellationToken token) => PlaceStructureAsync(tripId, regionId, "order", true, token);

    /// <summary>Applies one original-content-bound Place vector without scalar or route replacement.</summary>
    [HttpPut("{tripId:guid}/conditional/regions/{regionId:guid}/places/order")]
    public Task<IActionResult> ApplyPlaceOrder(Guid tripId, Guid regionId, CancellationToken token) => PlaceStructureAsync(tripId, regionId, "order", false, token);

    private async Task<IActionResult> PlaceStructureAsync(Guid tripId, Guid targetId, string operation, bool prepare, CancellationToken token)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, token);
        if (user == null) return Unauthorized();
        try
        {
            // Ownership precedes request parsing, concealing foreign/missing resources equally.
            var owned = operation == "order"
                ? await _dbContext.Regions.AnyAsync(
                    r => r.Id == targetId && r.TripId == tripId && r.UserId == user.Id && r.Trip.UserId == user.Id, token)
                : await _dbContext.Places.AnyAsync(
                    p => p.Id == targetId && p.UserId == user.Id && p.Region.TripId == tripId && p.Region.UserId == user.Id && p.Region.Trip.UserId == user.Id, token);
            if (!owned) return NotFound();
            var request = await PlaceStructuralRequest.ReadAsync(Request.Body, operation, false, token);
            if (prepare) return Ok(await _lifecycle.PreparePlaceStructureAsync(tripId, targetId, user.Id, request, new HashSet<string>(), new HashSet<string>(), token));
            var result = await _lifecycle.ApplyPlaceStructureAsync(tripId, targetId, user.Id, request, new HashSet<string>(), new HashSet<string>(),
                current => authority.RevalidateMutationAsync(HttpContext, user.Id, current), null, token);
            return Ok(new { contract = result.Contract, success = result.Success, applied = true, data = result.Data, affected = result.Affected, deletedIds = result.DeletedIds, warnings = result.Warnings });
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}

public sealed partial class TripEditorController
{
    /// <summary>Prepares one combined Web edit; it cannot silently adopt a new scalar, membership or private dependency base.</summary>
    [HttpPost("places/{placeId:guid}/prepare")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PreparePlaceUpdate(Guid tripId, Guid placeId, CancellationToken token)
    {
        var failure = RequireEditorUser(out var userId);
        if (failure != null) return failure;
        try { return Ok(await _placeMutations.PrepareStructureAsync(tripId, placeId, userId!, Request.Body, "web-update", token)); }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }

    /// <summary>Prepares exact scoped material content and orders; Web canonical restrictions remain enforced.</summary>
    [HttpPost("regions/{regionId:guid}/places/order/prepare")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PrepareEditorPlaceOrder(Guid tripId, Guid regionId, CancellationToken token)
    {
        var failure = RequireEditorUser(out var userId);
        if (failure != null) return failure;
        try { return Ok(await _placeMutations.PrepareStructureAsync(tripId, regionId, userId!, Request.Body, "order", token)); }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}
