using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>Issues, executes or recovers a protected area-order submission.</summary>
    [HttpPut("regions/{targetId:guid}/areas/order/receipt")]
    [HttpPost("regions/{targetId:guid}/areas/order/receipt/operations")]
    [HttpGet("regions/{targetId:guid}/areas/order/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedAreaOrder(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("region", targetId), "area-order", "PUT", operationId, ProtectedAreas().OrderAsync, token);

    /// <summary>Issues, executes or recovers a protected area-delete submission.</summary>
    [HttpDelete("areas/{targetId:guid}/receipt")]
    [HttpPost("areas/{targetId:guid}/receipt/operations")]
    [HttpGet("areas/{targetId:guid}/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedAreaDeletion(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("area", targetId), "area-delete", "DELETE", operationId, ProtectedAreas().DeleteAsync, token);

    /// <summary>Issues, executes or recovers a protected area-create submission.</summary>
    [HttpPost("regions/{targetId:guid}/areas/receipt")]
    [HttpPost("regions/{targetId:guid}/areas/receipt/operations")]
    [HttpGet("regions/{targetId:guid}/areas/receipt/operations/{operationId:guid}")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedAreaCreation(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("region", targetId), "area-create", "POST", operationId, ProtectedAreas().CreateAsync, token);

    /// <summary>Prepares consent against the client-held Area and sibling originals.</summary>
    [HttpPost("areas/{targetId:guid}/deletion-preview")]
    [AutoValidateAntiforgeryToken]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> PreviewProtectedAreaDeletion(Guid tripId, Guid targetId, CancellationToken token)
    {
        var failure = RequireEditorUser(out var subject);
        if (failure != null) return Task.FromResult(failure);
        var routes = new TripStructureReceiptRoutes(Request, _dbContext, subject!, true, TripMetadataReceiptRoutes.CookieCredential(HttpContext));
        return routes.PreviewAsync(tripId, targetId, ProtectedAreas().PreviewAsync, token);
    }

    /// <summary>Uses the application protection ring and server clock for signed Area deletion previews.</summary>
    private TripStructureAreas ProtectedAreas() =>
        new(_dbContext, HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>(),
            HttpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);
}
