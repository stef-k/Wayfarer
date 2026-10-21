using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>Cookie/antiforgery opt-in metadata save, sharing the bearer's locked per-field compare/apply authority.</summary>
    [HttpPatch("metadata/conditional")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PatchConditionalMetadata(Guid tripId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null) return authFailure;
        try
        {
            var result = await new TripMetadataMutationService(_dbContext)
                .ApplyAsync(tripId, userId!, Request.Body, true, null, cancellationToken);
            var trip = result.Trip;
            if (result.Applied)
            {
                _thumbnailGenerator.InvalidateThumbnails(tripId, trip.UpdatedAt);
                await _warmupScheduler.ScheduleWarmupAsync(tripId, immediate: result.ImagesIntroduced);
            }
            var metadata = EditorTripStateMapper.ToMetadata(trip,
                trip.IsPublic ? GeneratePublicTripUrl(tripId) : null,
                trip.IsPublic && trip.ShareProgressEnabled ? GenerateProgressPublicTripUrl(tripId) : null);
            var warnings = result.DisabledShareProgress
                ? new[] { new EditorWarningDto("share-progress-disabled", "Share progress was disabled because the trip is private.", "trip", tripId.ToString()) }
                : Array.Empty<EditorWarningDto>();
            return Ok(new EditorMutationResult<EditorTripMetadataDto>(true, metadata,
                EditorAffectedSlicesDto.MetadataOnly(metadata), EditorDeletedIdsDto.Empty, warnings));
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}
