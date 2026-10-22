using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Opt-in bearer Place scalars with current credential, parent and selected originals checked under lock.</summary>
    [HttpPatch("{tripId:guid}/conditional/places/{placeId:guid}")]
    public async Task<IActionResult> PatchConditionalPlace(Guid tripId, Guid placeId,
        [FromServices] IWebHostEnvironment environment, [FromServices] IIconColorProvider colors, CancellationToken cancellationToken)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, cancellationToken);
        if (user == null) return Unauthorized();
        try
        {
            var result = await new PlaceConditionalMutationService(_dbContext, environment, colors).ApplyAsync(
                tripId, placeId, user.Id, Request.Body, false,
                token => authority.RevalidateMutationAsync(HttpContext, user.Id, token), cancellationToken);
            // Legacy bearer Place updates warm Notes images after commit. No-op/rejection schedules nothing.
            if (result.Applied) await _warmupScheduler.ScheduleWarmupAsync(tripId);
            var place = result.Place;
            return Ok(new { contract = "bd2-1", success = true, applied = result.Applied, place = new
            {
                place.Id, place.RegionId, place.Name, notes = RichNotes.Normalize(place.Notes), place.IconName, place.MarkerColor
            } });
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}

public sealed partial class TripEditorController
{
    /// <summary>Cookie/User/antiforgery scalar edits; structural/combined edits retain their existing complete PUT.</summary>
    [HttpPatch("places/{placeId:guid}/conditional")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PatchConditionalPlace(Guid tripId, Guid placeId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null) return authFailure;
        try
        {
            var result = await new PlaceConditionalMutationService(_dbContext, _environment, _iconColorProvider)
                .ApplyAsync(tripId, placeId, userId!, Request.Body, true, null, cancellationToken);
            // The existing Web Place writer schedules no external work. Reader failures after commit stay unknown.
            var reader = new TripEditorPlaceMutationReader(_dbContext);
            var dto = await reader.LoadPlaceDtoAsync(result.Place.Id, tripId, userId!, cancellationToken);
            var affected = await reader.BuildAffectedAsync(tripId, userId!, [dto], [], null, null, false, cancellationToken);
            return Ok(new EditorMutationResult<EditorPlaceDto>(true, dto, affected, EditorDeletedIdsDto.Empty, []));
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}
