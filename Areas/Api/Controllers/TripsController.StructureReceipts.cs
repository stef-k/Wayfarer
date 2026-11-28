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

    /// <summary>Issues, executes or recovers a protected region-create submission.</summary>
    [HttpPost("{tripId:guid}/conditional/regions/receipt")]
    [HttpPost("{tripId:guid}/conditional/regions/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/regions/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedRegionCreation(Guid tripId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("trip", tripId), "region-create", "POST", operationId,
            write => new TripStructureCollections(_dbContext).CreateRegionAsync(write), token, serializable: true);

    /// <summary>Issues, executes or recovers a protected region-order submission.</summary>
    [HttpPut("{tripId:guid}/conditional/regions/order/receipt")]
    [HttpPost("{tripId:guid}/conditional/regions/order/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/regions/order/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedRegionOrder(Guid tripId, Guid? operationId, [FromServices] LifecycleDependencyConfirmation confirmation, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("trip", tripId), "region-order", "PUT", operationId,
            write => new TripStructureCollections(_dbContext).OrderRegionsAsync(write, confirmation), token);

    /// <summary>Issues, executes or recovers a protected place-create submission.</summary>
    [HttpPost("{tripId:guid}/conditional/places/receipt")]
    [HttpPost("{tripId:guid}/conditional/places/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/places/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedPlaceCreation(Guid tripId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("trip", tripId), "place-create", "POST", operationId,
            write => new TripStructureCollections(_dbContext).CreateBearerPlaceAsync(write), token, serializable: true);

    /// <summary>Requires the current incoming credential for domain admission and historical disclosure.</summary>
    private async Task<IActionResult> DispatchStructureAsync(Guid tripId, TripReceiptTarget target, string kind, string method,
        Guid? operationId, Func<TripStructureWrite, Task<TripStructureResult>> apply, CancellationToken token, bool serializable = false)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, token);
        if (user == null) return Unauthorized();
        var routes = new TripStructureReceiptRoutes(Request, _dbContext, user.Id, false,
            (_, ct) => authority.RevalidateMutationAsync(HttpContext, user.Id, ct));
        return await routes.DispatchAsync(tripId, target, kind, method, operationId, apply,
            kind == "place-create" ? null : () => _warmupScheduler.ScheduleWarmupAsync(tripId), token, serializable);
    }
}
