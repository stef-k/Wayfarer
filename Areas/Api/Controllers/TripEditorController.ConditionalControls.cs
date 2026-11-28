using Microsoft.AspNetCore.Mvc;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>Executes a frozen Trip control submission.</summary>
    [HttpPut("tags/conditional")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> ExecuteReplaceTags(Guid tripId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.ExecuteAsync(tripId, token), "tags-replace");

    /// <summary>Issues a permit bound to this Trip control.</summary>
    [HttpPost("tags/conditional/operations")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> IssueReplaceTags(Guid tripId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.IssueAsync(tripId, token), "tags-replace");

    /// <summary>Reads historical evidence for this Trip control.</summary>
    [HttpGet("tags/conditional/operations/{operationId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> LookupReplaceTags(Guid tripId, Guid operationId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.LookupAsync(tripId, operationId, token), "tags-replace");

    /// <summary>Executes a frozen Trip control submission.</summary>
    [HttpPatch("share-progress/conditional")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> ExecuteEditorSharing(Guid tripId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.ExecuteAsync(tripId, token), "editor-sharing");

    /// <summary>Issues a permit bound to this Trip control.</summary>
    [HttpPost("share-progress/conditional/operations")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> IssueEditorSharing(Guid tripId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.IssueAsync(tripId, token), "editor-sharing");

    /// <summary>Reads historical evidence for this Trip control.</summary>
    [HttpGet("share-progress/conditional/operations/{operationId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> LookupEditorSharing(Guid tripId, Guid operationId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.LookupAsync(tripId, operationId, token), "editor-sharing");
}
