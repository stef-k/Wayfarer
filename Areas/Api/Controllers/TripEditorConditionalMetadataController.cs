using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public sealed partial class TripEditorController
{
    /// <summary>Cookie/antiforgery opt-in metadata save, sharing the bearer's locked per-field compare/apply authority.</summary>
    [HttpPatch("metadata/conditional")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> PatchConditionalMetadata(Guid tripId, CancellationToken cancellationToken)
    {
        var authFailure = RequireEditorUser(out var userId);
        if (authFailure != null) return authFailure;
        try
        {
            var routes = WebMetadataRoutes(userId!);
            var (execution, identity) = await routes.PatchAsync(tripId, cancellationToken);
            if (execution.Mutation is { Applied: true } applied)
            {
                _thumbnailGenerator.InvalidateThumbnails(tripId, applied.Trip.UpdatedAt);
                await _warmupScheduler.ScheduleWarmupAsync(tripId, immediate: applied.ImagesIntroduced);
            }
            if (execution.Evidence != null && identity != null)
                return TripMetadataReceiptRoutes.EvidenceResult(execution.Evidence, identity);
            var result = execution.Mutation ?? throw new InvalidOperationException("Missing Trip metadata result.");
            var trip = result.Trip;
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

    /// <summary>Issues one Name/Notes permit for an authenticated owner session and its antiforgery proof.</summary>
    [HttpPost("metadata/conditional/operations")]
    [ValidateAntiForgeryToken]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> IssueMetadataOperation(Guid tripId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.IssueAsync(tripId, token));

    /// <summary>Observes a Web operation even if the account can no longer load the Trip.</summary>
    [HttpGet("metadata/conditional/operations/{operationId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> LookupMetadataOperation(Guid tripId, Guid operationId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.LookupAsync(tripId, operationId, token));

    /// <summary>Reads fresh owner comparison values without loading Regions, geometry or external providers.</summary>
    [HttpGet("metadata/conditional")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> ReadConditionalMetadata(Guid tripId, CancellationToken token)
    {
        var failure = RequireEditorUser(out var userId);
        if (failure != null) return failure;
        var trip = await _dbContext.Trips.AsNoTracking()
            .SingleOrDefaultAsync(trip => trip.Id == tripId && trip.UserId == userId, token);
        if (trip == null) return NotFound();
        return Ok(new
        {
            subjectId = userId,
            metadata = EditorTripStateMapper.ToMetadata(trip, trip.IsPublic ? GeneratePublicTripUrl(tripId) : null,
                trip.IsPublic && trip.ShareProgressEnabled ? GenerateProgressPublicTripUrl(tripId) : null)
        });
    }

    /// <summary>Applies the Editor's current cookie and User authority to receipt routes.</summary>
    private Task<IActionResult> WithMetadataAuthorityAsync(Func<TripMetadataReceiptRoutes, Task<IActionResult>> dispatch)
    {
        var failure = RequireEditorUser(out var userId);
        return failure == null ? dispatch(WebMetadataRoutes(userId!)) : Task.FromResult(failure);
    }

    /// <summary>Binds the authenticated cookie's stamp to the account row read under the final lock.</summary>
    private TripMetadataReceiptRoutes WebMetadataRoutes(string userId)
    {
        var claim = HttpContext.RequestServices.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity.SecurityStampClaimType;
        var stamp = User.FindFirst(claim)?.Value;
        return new(Request, _dbContext, userId, true, (account, _) =>
            Task.FromResult(!string.IsNullOrEmpty(stamp) && string.Equals(stamp, account.SecurityStamp, StringComparison.Ordinal)));
    }
}
