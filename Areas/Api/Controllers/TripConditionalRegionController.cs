using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models.Dtos;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Opt-in bearer Name/Notes editing, with current authority and original-value comparison under lock.</summary>
    [HttpPatch("{tripId:guid}/conditional/regions/{regionId:guid}")]
    public async Task<IActionResult> PatchConditionalRegion(Guid tripId, Guid regionId, CancellationToken cancellationToken)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, cancellationToken);
        if (user == null) return Unauthorized();
        try
        {
            var result = await new RegionConditionalMutationService(_dbContext).ApplyAsync(tripId, regionId, user.Id, Request.Body, false,
                token => authority.RevalidateMutationAsync(HttpContext, user.Id, token), cancellationToken);
            if (result.Applied) await _warmupScheduler.ScheduleWarmupAsync(tripId);
            var region = result.Region;
            return Ok(new
            {
                contract = "bd2-1", success = true, applied = result.Applied,
                region = new ApiTripRegionDto
                {
                    Id = region.Id, Name = region.Name, Notes = RichNotes.Normalize(region.Notes),
                    CoverImageUrl = region.CoverImageUrl, DisplayOrder = region.DisplayOrder,
                    Center = region.Center == null ? null : [region.Center.X, region.Center.Y]
                }
            });
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}

public sealed partial class TripEditorController
{
    /// <summary>Cookie/User/antiforgery Region scalar editing through the same compare/apply authority as bearer.</summary>
    [HttpPatch("regions/{regionId:guid}/conditional")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PatchConditionalRegion(Guid tripId, Guid regionId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null) return authFailure;
        try
        {
            var result = await new RegionConditionalMutationService(_dbContext)
                .ApplyAsync(tripId, regionId, userId!, Request.Body, true, null, cancellationToken);
            var dto = EditorTripStateMapper.ToRegion(result.Region);
            var affected = new EditorAffectedSlicesDto(null, [dto], null, [], new Dictionary<Guid, IReadOnlyList<Guid>>(),
                [], new Dictionary<Guid, IReadOnlyList<Guid>>(), [], null, [], null, null, null);
            // Existing Web Region saves do not schedule external work; bearer retains its post-commit warm-up.
            return Ok(new EditorMutationResult<EditorRegionDto>(true, dto, affected, EditorDeletedIdsDto.Empty, []));
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}
