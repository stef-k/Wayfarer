using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Opt-in bearer Area Notes with current credential, parent and original-value checks under lock.</summary>
    [HttpPatch("{tripId:guid}/conditional/areas/{areaId:guid}")]
    public async Task<IActionResult> PatchConditionalArea(Guid tripId, Guid areaId, CancellationToken cancellationToken)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, cancellationToken);
        if (user == null) return Unauthorized();
        try
        {
            var result = await new AreaConditionalMutationService(_dbContext).ApplyAsync(tripId, areaId, user.Id, Request.Body, false,
                token => authority.RevalidateMutationAsync(HttpContext, user.Id, token), cancellationToken);
            if (result.Applied) await _warmupScheduler.ScheduleWarmupAsync(tripId);
            return Ok(new { contract = "bd2-1", success = true, applied = result.Applied, area = result.Area.ToApiDto() });
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}

public sealed partial class TripEditorController
{
    /// <summary>Cookie/User/antiforgery dirty Area fields through the same server-owned compare/apply boundary.</summary>
    [HttpPatch("areas/{areaId:guid}/conditional")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PatchConditionalArea(Guid tripId, Guid areaId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null) return authFailure;
        try
        {
            var result = await new AreaConditionalMutationService(_dbContext)
                .ApplyAsync(tripId, areaId, userId!, Request.Body, true, null, cancellationToken);
            var dto = EditorTripStateMapper.ToArea(tripId, result.Area.RegionId, result.Area);
            var affected = new EditorAffectedSlicesDto(null, [], null, [], new Dictionary<Guid, IReadOnlyList<Guid>>(),
                [dto], new Dictionary<Guid, IReadOnlyList<Guid>>(), [], null, [], null, null, null);
            // Existing Web Area saves schedule no external work and do not replace sibling order.
            return Ok(new EditorMutationResult<EditorAreaDto>(true, dto, affected, EditorDeletedIdsDto.Empty, []));
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}
