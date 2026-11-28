using Microsoft.AspNetCore.Mvc;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Issues, executes or recovers a protected place-coordinates submission.</summary>
    [HttpPost("{tripId:guid}/conditional/places/{targetId:guid}/coordinates/receipt")]
    [HttpPost("{tripId:guid}/conditional/places/{targetId:guid}/coordinates/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/places/{targetId:guid}/coordinates/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedPlaceCoordinates(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("place", targetId), "place-coordinates", "POST", operationId,
            write => _lifecycle.PlaceStructureWithReceiptAsync(write, "coordinates", new HashSet<string>(), new HashSet<string>()), token);

    /// <summary>Issues, executes or recovers a protected place-move submission.</summary>
    [HttpPost("{tripId:guid}/conditional/places/{targetId:guid}/move/receipt")]
    [HttpPost("{tripId:guid}/conditional/places/{targetId:guid}/move/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/places/{targetId:guid}/move/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedPlaceMove(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("place", targetId), "place-move", "POST", operationId,
            write => _lifecycle.PlaceStructureWithReceiptAsync(write, "move", new HashSet<string>(), new HashSet<string>()), token);

    /// <summary>Issues, executes or recovers a protected place-order submission.</summary>
    [HttpPut("{tripId:guid}/conditional/regions/{targetId:guid}/places/order/receipt")]
    [HttpPost("{tripId:guid}/conditional/regions/{targetId:guid}/places/order/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/regions/{targetId:guid}/places/order/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedPlaceOrder(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("region", targetId), "place-order", "PUT", operationId,
            write => _lifecycle.PlaceStructureWithReceiptAsync(write, "order", new HashSet<string>(), new HashSet<string>()), token);
}
