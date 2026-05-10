using Microsoft.AspNetCore.Mvc;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>
    /// Creates an area in a normal owned region.
    /// </summary>
    [HttpPost("regions/{regionId:guid}/areas")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateArea(Guid tripId, Guid regionId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _areaMutations.CreateAreaAsync(tripId, regionId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
    }

    /// <summary>
    /// Updates complete editable fields for one owned area.
    /// </summary>
    [HttpPut("areas/{areaId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateArea(Guid tripId, Guid areaId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _areaMutations.UpdateAreaAsync(tripId, areaId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
    }

    /// <summary>
    /// Replaces only the polygon geometry for one owned area.
    /// </summary>
    [HttpPut("areas/{areaId:guid}/geometry")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateAreaGeometry(Guid tripId, Guid areaId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _areaMutations.UpdateAreaGeometryAsync(tripId, areaId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
    }

    /// <summary>
    /// Deletes one owned area.
    /// </summary>
    [HttpDelete("areas/{areaId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteArea(Guid tripId, Guid areaId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _areaMutations.DeleteAreaAsync(tripId, areaId, userId!, cancellationToken);
        return ToActionResult(outcome);
    }

    /// <summary>
    /// Persists the complete desired area order for one normal owned region.
    /// </summary>
    [HttpPut("regions/{regionId:guid}/areas/order")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OrderAreas(Guid tripId, Guid regionId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _areaMutations.OrderAreasAsync(tripId, regionId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
    }
}
