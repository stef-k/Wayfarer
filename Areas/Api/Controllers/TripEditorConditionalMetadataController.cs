using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models.Dtos;
using Wayfarer.Services.TripMetadataReceipts;
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
        var authFailure = RequireEditorUser(out _);
        if (authFailure != null) return authFailure;
        try
        {
            return await WithMetadataAuthorityAsync(async routes =>
            {
                var (execution, identity) = await routes.PatchAsync(tripId, cancellationToken);
                if (execution.Mutation is { Applied: true } applied)
                {
                    _thumbnailGenerator.InvalidateThumbnails(tripId, applied.Trip.UpdatedAt);
                    await _warmupScheduler.ScheduleWarmupAsync(tripId, immediate: applied.ImagesIntroduced);
                }
                return TripMetadataReceiptRoutes.EvidenceResult(
                    execution.Evidence ?? throw new InvalidOperationException("Missing Trip metadata evidence."),
                    identity ?? throw new InvalidOperationException("Missing Trip metadata identity."));
            });
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
        var query = _dbContext.Trips.AsNoTracking();
        var v2 = Request.Query["contract"] == TripMetadataIdentity.Version2;
        if (v2) query = query.Include(trip => trip.Tags).AsSingleQuery();
        var trip = await query.SingleOrDefaultAsync(trip => trip.Id == tripId && trip.UserId == userId, token);
        if (trip == null) return NotFound();
        var metadata = EditorTripStateMapper.ToMetadata(trip, trip.IsPublic ? GeneratePublicTripUrl(tripId) : null,
            trip.IsPublic && trip.ShareProgressEnabled ? GenerateProgressPublicTripUrl(tripId) : null);
        if (!v2) return Ok(new { subjectId = userId, metadata });
        var tags = trip.Tags.OrderBy(tag => tag.Slug, StringComparer.Ordinal)
            .ThenBy(tag => tag.Id).Select(tag => new TripTagDto(tag.Id, tag.Name, tag.Slug)).ToArray();
        return Ok(new { subjectId = userId, metadata, tags });
    }

    /// <summary>Applies the current cookie and User authority to receipt routes.</summary>
    private Task<IActionResult> WithMetadataAuthorityAsync(Func<TripMetadataReceiptRoutes, Task<IActionResult>> dispatch,
        string kind = TripMetadataIdentity.OperationKind) =>
        TripMetadataReceiptRoutes.WithWebAuthorityAsync(HttpContext, _dbContext, kind, dispatch);
}
