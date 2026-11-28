using Microsoft.AspNetCore.Mvc;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Executes a frozen Trip control submission.</summary>
    [HttpPut("{tripId:guid}/conditional/tags")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> ExecuteReplaceTags(Guid tripId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.ExecuteAsync(tripId, token), token, "tags-replace");

    /// <summary>Issues a permit bound to this Trip control.</summary>
    [HttpPost("{tripId:guid}/conditional/tags/operations")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> IssueReplaceTags(Guid tripId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.IssueAsync(tripId, token), token, "tags-replace");

    /// <summary>Reads historical evidence for this Trip control.</summary>
    [HttpGet("{tripId:guid}/conditional/tags/operations/{operationId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> LookupReplaceTags(Guid tripId, Guid operationId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.LookupAsync(tripId, operationId, token), token, "tags-replace");

    /// <summary>Executes a frozen Trip control submission.</summary>
    [HttpPut("{tripId:guid}/conditional/saved-view")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> ExecuteSavedView(Guid tripId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.ExecuteAsync(tripId, token), token, "saved-view");

    /// <summary>Issues a permit bound to this Trip control.</summary>
    [HttpPost("{tripId:guid}/conditional/saved-view/operations")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> IssueSavedView(Guid tripId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.IssueAsync(tripId, token), token, "saved-view");

    /// <summary>Reads historical evidence for this Trip control.</summary>
    [HttpGet("{tripId:guid}/conditional/saved-view/operations/{operationId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> LookupSavedView(Guid tripId, Guid operationId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.LookupAsync(tripId, operationId, token), token, "saved-view");
}
