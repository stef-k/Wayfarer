using Microsoft.AspNetCore.Mvc;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>Issues, executes or recovers a protected place-create submission.</summary>
    [HttpPost("regions/{targetId:guid}/places/conditional/receipt")]
    [HttpPost("regions/{targetId:guid}/places/conditional/receipt/operations")]
    [HttpGet("regions/{targetId:guid}/places/conditional/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedPlaceCreation(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("region", targetId), "place-create", "POST", operationId,
            _placeMutations.CreateWithReceiptAsync, token, serializable: true, prepareProvider: _placeMutations.PrepareReceiptCreationProviderAsync);

    /// <summary>Issues, executes or recovers a protected place-save submission.</summary>
    [HttpPut("places/{targetId:guid}/receipt")]
    [HttpPost("places/{targetId:guid}/receipt/operations")]
    [HttpGet("places/{targetId:guid}/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedPlaceSave(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("place", targetId), "place-save", "PUT", operationId,
            write => _placeMutations.StructureWithReceiptAsync(write, "web-update"), token, prepareProvider: _placeMutations.PrepareReceiptSaveProviderAsync);

    /// <summary>Issues, executes or recovers a protected place-order submission.</summary>
    [HttpPut("regions/{targetId:guid}/places/order/receipt")]
    [HttpPost("regions/{targetId:guid}/places/order/receipt/operations")]
    [HttpGet("regions/{targetId:guid}/places/order/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedPlaceOrder(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("region", targetId), "place-order", "PUT", operationId,
            write => _placeMutations.StructureWithReceiptAsync(write, "order"), token);
}
