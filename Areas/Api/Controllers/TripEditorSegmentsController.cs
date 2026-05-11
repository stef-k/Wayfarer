using Microsoft.AspNetCore.Mvc;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>
    /// Creates a trip-level segment in an owned trip.
    /// </summary>
    [HttpPost("segments")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateSegment(Guid tripId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _segmentMutations.CreateSegmentAsync(tripId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
    }

    /// <summary>
    /// Updates complete editable fields for one owned segment.
    /// </summary>
    [HttpPut("segments/{segmentId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> UpdateSegment(Guid tripId, Guid segmentId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _segmentMutations.UpdateSegmentAsync(tripId, segmentId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
    }

    /// <summary>
    /// Deletes one owned segment.
    /// </summary>
    [HttpDelete("segments/{segmentId:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSegment(Guid tripId, Guid segmentId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _segmentMutations.DeleteSegmentAsync(tripId, segmentId, userId!, cancellationToken);
        return ToActionResult(outcome);
    }

    /// <summary>
    /// Persists the complete desired trip-level segment order.
    /// </summary>
    [HttpPut("segments/order")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> OrderSegments(Guid tripId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null)
        {
            return authFailure;
        }

        var outcome = await _segmentMutations.OrderSegmentsAsync(tripId, userId!, Request.Body, cancellationToken);
        return ToActionResult(outcome);
    }
}
