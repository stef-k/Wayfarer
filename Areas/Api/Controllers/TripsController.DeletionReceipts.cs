using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Issues, executes or recovers one confirmed Region deletion.</summary>
    [HttpPost("{tripId:guid}/regions/{targetId:guid}/deletion/receipt")]
    [HttpPost("{tripId:guid}/regions/{targetId:guid}/deletion/receipt/operations")]
    [HttpGet("{tripId:guid}/regions/{targetId:guid}/deletion/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedRegionDeletion(Guid tripId, Guid targetId, Guid? operationId,
        CancellationToken token) =>
        DispatchStructureAsync(tripId, new("region", targetId), "region-delete", "POST", operationId,
            _lifecycle.DeleteRegionWithReceiptAsync, token);

    /// <summary>Issues, executes or recovers one confirmed Place deletion.</summary>
    [HttpPost("{tripId:guid}/conditional/places/{targetId:guid}/deletion/receipt")]
    [HttpPost("{tripId:guid}/conditional/places/{targetId:guid}/deletion/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/places/{targetId:guid}/deletion/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedPlaceDeletion(Guid tripId, Guid targetId, Guid? operationId,
        CancellationToken token) =>
        DispatchStructureAsync(tripId, new("place", targetId), "place-delete", "POST", operationId,
            _lifecycle.DeletePlaceWithReceiptAsync, token);
}
