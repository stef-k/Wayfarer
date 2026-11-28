using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>Issues, executes or recovers one confirmed Region deletion.</summary>
    [HttpPost("regions/{targetId:guid}/deletion/receipt")]
    [HttpPost("regions/{targetId:guid}/deletion/receipt/operations")]
    [HttpGet("regions/{targetId:guid}/deletion/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedRegionDeletion(Guid tripId, Guid targetId, Guid? operationId,
        [FromServices] PlaceRegionLifecycleService lifecycle, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("region", targetId), "region-delete", "POST", operationId,
            lifecycle.DeleteRegionWithReceiptAsync, token);

    /// <summary>Issues, executes or recovers one confirmed Place deletion.</summary>
    [HttpPost("places/{targetId:guid}/deletion/receipt")]
    [HttpPost("places/{targetId:guid}/deletion/receipt/operations")]
    [HttpGet("places/{targetId:guid}/deletion/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedPlaceDeletion(Guid tripId, Guid targetId, Guid? operationId,
        [FromServices] PlaceRegionLifecycleService lifecycle, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("place", targetId), "place-delete", "POST", operationId,
            lifecycle.DeletePlaceWithReceiptAsync, token);
}
