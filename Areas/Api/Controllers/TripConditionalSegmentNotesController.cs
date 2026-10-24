using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Opt-in bearer Notes-only compare/apply; the legacy Segment PUT stays unconditional.</summary>
    [HttpPatch("{tripId:guid}/conditional/segments/{segmentId:guid}")]
    public async Task<IActionResult> PatchConditionalSegmentNotes(Guid tripId, Guid segmentId, CancellationToken cancellationToken)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, cancellationToken);
        if (user == null) return Unauthorized();
        try
        {
            var result = await new SegmentNotesConditionalMutationService(_dbContext).ApplyAsync(tripId, segmentId, user.Id, Request.Body, false,
                token => authority.RevalidateMutationAsync(HttpContext, user.Id, token), cancellationToken);
            // Like the legacy Segment Notes writer, this operation schedules no external work.
            return Ok(new { contract = "bd2-1", success = true, applied = result.Applied,
                segment = new { id = result.Segment.Id, notes = Wayfarer.Util.RichNotes.Normalize(result.Segment.Notes) } });
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}

public sealed partial class TripEditorController
{
    /// <summary>Cookie/User/antiforgery Notes-only intent using the same current-authority transaction as bearer.</summary>
    [HttpPatch("segments/{segmentId:guid}/conditional")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PatchConditionalSegmentNotes(Guid tripId, Guid segmentId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null) return authFailure;
        try
        {
            var result = await new SegmentNotesConditionalMutationService(_dbContext)
                .ApplyAsync(tripId, segmentId, userId!, Request.Body, true, null, cancellationToken);
            var capabilities = _externalRoutingCapabilities == null ? null
                : await _externalRoutingCapabilities.ProjectAsync(userId!, [result.Segment], cancellationToken);
            var dto = EditorTripStateMapper.ToSegment(tripId, result.Segment,
                _segmentMutations.IssueAggregateToken(userId!, tripId, result.Segment), true,
                capabilities?.GetValueOrDefault(segmentId), _segmentMutations.IssueRouteToken(userId!, tripId, result.Segment));
            var affected = new EditorAffectedSlicesDto(null, [], null, [], new Dictionary<Guid, IReadOnlyList<Guid>>(),
                [], new Dictionary<Guid, IReadOnlyList<Guid>>(), [dto], null, [], null, null, null);
            return Ok(new EditorMutationResult<EditorSegmentDto>(true, dto, affected, EditorDeletedIdsDto.Empty, []));
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}
