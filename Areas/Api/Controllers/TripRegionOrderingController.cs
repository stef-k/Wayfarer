using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Prepares retained normal-Region material/order and canonical identity/order evidence without rebasing it.</summary>
    [HttpPost("{tripId:guid}/conditional/regions/order/prepare")]
    public Task<IActionResult> PrepareRegionOrder(Guid tripId, CancellationToken token) => RegionOrderAsync(tripId, true, token);

    /// <summary>Atomically applies only normal-Region orders and current-safe Trip freshness.</summary>
    [HttpPut("{tripId:guid}/conditional/regions/order")]
    public Task<IActionResult> ApplyRegionOrder(Guid tripId, CancellationToken token) => RegionOrderAsync(tripId, false, token);

    /// <summary>Resolves incoming bearer authority separately from cookie authority and rechecks it after locks.</summary>
    private async Task<IActionResult> RegionOrderAsync(Guid tripId, bool prepare, CancellationToken token)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, token);
        if (user == null) return Unauthorized();
        if (!await _dbContext.Trips.AsNoTracking().AnyAsync(t => t.Id == tripId && t.UserId == user.Id, token)) return NotFound();
        try
        {
            var request = await RegionOrderRequest.ReadAsync(Request.Body, token);
            var result = await _lifecycle.OrderRegionsAsync(tripId, user.Id, request, false, prepare,
                current => authority.RevalidateMutationAsync(HttpContext, user.Id, current), token);
            if (prepare) return Ok(result);
            var applied = (Wayfarer.Models.Dtos.Editor.EditorMutationResult<object>)result;
            return Ok(new { applied.Contract, applied.Success, applied = true, applied.Data, applied.Affected, applied.DeletedIds, applied.Warnings });
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}

public sealed partial class TripEditorController
{
    /// <summary>Cookie/User/antiforgery preparation for the same original-bound Region order authority.</summary>
    [HttpPost("regions/order/prepare")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PrepareEditorRegionOrder(Guid tripId, CancellationToken token)
    {
        var failure = RequireEditorUser(out var userId);
        if (failure != null) return failure;
        try { return Ok(await _regionMutations.OrderRegionsAsync(tripId, userId!, Request.Body, true, token)); }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}
