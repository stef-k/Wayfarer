using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Applies only explicitly selected Name/Notes after comparing the original published field values.</summary>
    [HttpPatch("{tripId:guid}/conditional")]
    public async Task<IActionResult> PatchConditionalMetadata(Guid tripId, CancellationToken cancellationToken)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, cancellationToken);
        if (user == null) return Unauthorized();
        try
        {
            var result = await new TripMetadataMutationService(_dbContext).ApplyAsync(tripId, user.Id, Request.Body, false,
                token => authority.RevalidateMutationAsync(HttpContext, user.Id, token), cancellationToken);
            if (result.Applied && result.NotesRequested)
                await _warmupScheduler.ScheduleWarmupAsync(tripId, immediate: result.FirstImagesIntroduced);
            return Ok(new
            {
                contract = "bd2-1", success = true, applied = result.Applied,
                trip = new { result.Trip.Id, result.Trip.Name, Notes = RichNotes.Normalize(result.Trip.Notes), result.Trip.UpdatedAt }
            });
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}
