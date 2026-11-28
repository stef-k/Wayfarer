using Microsoft.AspNetCore.Mvc;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>Fences tag writes dispatched by an older Editor after cutover.</summary>
    [HttpPut("tags")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> PutTags(Guid tripId, CancellationToken cancellationToken) =>
        Task.FromResult(RequireEditorUser(out _) ?? TripMetadataReceiptRoutes.UpgradeRequired().ToActionResult());

    /// <summary>Fences sharing writes dispatched by an older Editor after cutover.</summary>
    [HttpPatch("share-progress")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> PatchShareProgress(Guid tripId, CancellationToken cancellationToken) =>
        Task.FromResult(RequireEditorUser(out _) ?? TripMetadataReceiptRoutes.UpgradeRequired().ToActionResult());
}
