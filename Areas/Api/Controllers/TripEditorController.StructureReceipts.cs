using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>Issues, executes or recovers only the protected region-fields operation on this adapter.</summary>
    [HttpPatch("regions/{targetId:guid}/conditional/receipt")]
    [HttpPost("regions/{targetId:guid}/conditional/receipt/operations")]
    [HttpGet("regions/{targetId:guid}/conditional/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedRegionFields(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("region", targetId), "region-fields", "PATCH", operationId,
            write => new TripStructureScalars(_dbContext).RegionAsync(write), token);

    /// <summary>Issues, executes or recovers only the protected place-fields operation on this adapter.</summary>
    [HttpPatch("places/{targetId:guid}/conditional/receipt")]
    [HttpPost("places/{targetId:guid}/conditional/receipt/operations")]
    [HttpGet("places/{targetId:guid}/conditional/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedPlaceFields(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("place", targetId), "place-fields", "PATCH", operationId,
            write => new TripStructureScalars(_dbContext).PlaceAsync(write, _environment, _iconColorProvider), token);

    /// <summary>Issues, executes or recovers only the protected area-fields operation on this adapter.</summary>
    [HttpPatch("areas/{targetId:guid}/conditional/receipt")]
    [HttpPost("areas/{targetId:guid}/conditional/receipt/operations")]
    [HttpGet("areas/{targetId:guid}/conditional/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedAreaFields(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("area", targetId), "area-fields", "PATCH", operationId,
            write => new TripStructureScalars(_dbContext).AreaAsync(write), token);

    /// <summary>Issues, executes or recovers only the protected area-geometry operation on this adapter.</summary>
    [HttpPut("areas/{targetId:guid}/geometry/receipt")]
    [HttpPost("areas/{targetId:guid}/geometry/receipt/operations")]
    [HttpGet("areas/{targetId:guid}/geometry/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedAreaGeometryFields(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("area", targetId), "area-geometry", "PUT", operationId,
            write => new TripStructureScalars(_dbContext).AreaAsync(write, geometryOnly: true), token);

    /// <summary>Issues, executes or recovers a protected region-create submission.</summary>
    [HttpPost("regions/conditional/receipt")]
    [HttpPost("regions/conditional/receipt/operations")]
    [HttpGet("regions/conditional/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedRegionCreation(Guid tripId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("trip", tripId), "region-create", "POST", operationId,
            write => new TripStructureCollections(_dbContext).CreateRegionAsync(write), token, serializable: true);

    /// <summary>Issues, executes or recovers a protected region-order submission.</summary>
    [HttpPut("regions/order/receipt")]
    [HttpPost("regions/order/receipt/operations")]
    [HttpGet("regions/order/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedRegionOrder(Guid tripId, Guid? operationId, [FromServices] LifecycleDependencyConfirmation confirmation, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("trip", tripId), "region-order", "PUT", operationId,
            write => new TripStructureCollections(_dbContext).OrderRegionsAsync(write, confirmation), token);

    /// <summary>Uses the current cookie stamp at every locked receipt barrier.</summary>
    private Task<IActionResult> DispatchStructureAsync(Guid tripId, TripReceiptTarget target, string kind, string method,
        Guid? operationId, Func<TripStructureWrite, Task<TripStructureResult>> apply, CancellationToken token, bool serializable = false,
        Func<TripStructureWrite, Func<CancellationToken, Task>, Task<Func<Task<TripPlaceProviderResult>>>>? prepareProvider = null)
    {
        var failure = RequireEditorUser(out var subject);
        if (failure != null) return Task.FromResult(failure);
        var routes = new TripStructureReceiptRoutes(Request, _dbContext, subject!, true, TripMetadataReceiptRoutes.CookieCredential(HttpContext));
        return routes.DispatchAsync(tripId, target, kind, method, operationId, apply, null, token, serializable, prepareProvider);
    }
}
