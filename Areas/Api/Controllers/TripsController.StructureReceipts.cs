using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Issues, executes or recovers only the protected region-fields operation on this adapter.</summary>
    [HttpPatch("{tripId:guid}/conditional/regions/{targetId:guid}/receipt")]
    [HttpPost("{tripId:guid}/conditional/regions/{targetId:guid}/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/regions/{targetId:guid}/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedRegionFields(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("region", targetId), "region-fields", "PATCH", operationId,
            write => new TripStructureScalars(_dbContext).RegionAsync(write), token);

    /// <summary>Issues, executes or recovers only the protected place-fields operation on this adapter.</summary>
    [HttpPatch("{tripId:guid}/conditional/places/{targetId:guid}/receipt")]
    [HttpPost("{tripId:guid}/conditional/places/{targetId:guid}/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/places/{targetId:guid}/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedPlaceFields(Guid tripId, Guid targetId, Guid? operationId,
        [FromServices] IWebHostEnvironment environment, [FromServices] IIconColorProvider colors, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("place", targetId), "place-fields", "PATCH", operationId,
            write => new TripStructureScalars(_dbContext).PlaceAsync(write, environment, colors), token);

    /// <summary>Issues, executes or recovers only the protected area-fields operation on this adapter.</summary>
    [HttpPatch("{tripId:guid}/conditional/areas/{targetId:guid}/receipt")]
    [HttpPost("{tripId:guid}/conditional/areas/{targetId:guid}/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/areas/{targetId:guid}/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedAreaFields(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("area", targetId), "area-fields", "PATCH", operationId,
            write => new TripStructureScalars(_dbContext).AreaAsync(write), token);

    /// <summary>Requires the current incoming credential for domain admission and historical disclosure.</summary>
    private async Task<IActionResult> DispatchStructureAsync(Guid tripId, TripReceiptTarget target, string kind, string method,
        Guid? operationId, Func<TripStructureWrite, Task<TripStructureResult>> apply, CancellationToken token)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, token);
        if (user == null) return Unauthorized();
        var routes = new TripStructureReceiptRoutes(Request, _dbContext, user.Id, false,
            (_, ct) => authority.RevalidateMutationAsync(HttpContext, user.Id, ct));
        return await routes.DispatchAsync(tripId, target, kind, method, operationId, apply,
            () => _warmupScheduler.ScheduleWarmupAsync(tripId), token);
    }
}
