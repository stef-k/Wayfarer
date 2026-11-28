using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;
using Wayfarer.Services.TripMetadataReceipts;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Issues, executes or recovers a protected area-order submission.</summary>
    [HttpPut("{tripId:guid}/conditional/regions/{targetId:guid}/areas/order/receipt")]
    [HttpPost("{tripId:guid}/conditional/regions/{targetId:guid}/areas/order/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/regions/{targetId:guid}/areas/order/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedAreaOrder(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("region", targetId), "area-order", "PUT", operationId, ProtectedAreas().OrderAsync, token);

    /// <summary>Issues, executes or recovers a protected area-delete submission.</summary>
    [HttpPost("{tripId:guid}/conditional/areas/{targetId:guid}/deletion/receipt")]
    [HttpPost("{tripId:guid}/conditional/areas/{targetId:guid}/deletion/receipt/operations")]
    [HttpGet("{tripId:guid}/conditional/areas/{targetId:guid}/deletion/receipt/operations/{operationId:guid}")]
    [RequestSizeLimit(262144)]
    public Task<IActionResult> ProtectedAreaDeletion(Guid tripId, Guid targetId, Guid? operationId, CancellationToken token) =>
        DispatchStructureAsync(tripId, new("area", targetId), "area-delete", "POST", operationId, ProtectedAreas().DeleteAsync, token);

    /// <summary>Prepares consent against the client-held Area and sibling originals.</summary>
    [HttpPost("{tripId:guid}/conditional/areas/{targetId:guid}/deletion-preview")]
    [RequestSizeLimit(262144)]
    public async Task<IActionResult> PreviewProtectedAreaDeletion(Guid tripId, Guid targetId, CancellationToken token)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, token);
        if (user == null) return Unauthorized();
        var routes = new TripStructureReceiptRoutes(Request, _dbContext, user.Id, false,
            (_, ct) => authority.RevalidateMutationAsync(HttpContext, user.Id, ct));
        return await routes.PreviewAsync(tripId, targetId, ProtectedAreas().PreviewAsync, token);
    }

    /// <summary>Uses the application protection ring and server clock for signed Area deletion previews.</summary>
    private TripStructureAreas ProtectedAreas() =>
        new(_dbContext, HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>(),
            HttpContext.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System);
}
