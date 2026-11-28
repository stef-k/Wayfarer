using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Applies only explicitly selected Name/Notes after comparing the original published field values.</summary>
    [HttpPatch("{tripId:guid}/conditional")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> PatchConditionalMetadata(Guid tripId, CancellationToken cancellationToken)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, cancellationToken);
        if (user == null) return Unauthorized();
        try
        {
            var routes = new TripMetadataReceiptRoutes(Request, _dbContext, user.Id, false,
                (_, token) => authority.RevalidateMutationAsync(HttpContext, user.Id, token));
            var (execution, identity) = await routes.PatchAsync(tripId, cancellationToken);
            if (execution.Mutation is { Applied: true, NotesRequested: true } applied)
                await _warmupScheduler.ScheduleWarmupAsync(tripId, immediate: applied.FirstImagesIntroduced);
            if (execution.Evidence != null && identity != null)
                return TripMetadataReceiptRoutes.EvidenceResult(execution.Evidence, identity);
            var result = execution.Mutation ?? throw new InvalidOperationException("Missing Trip metadata result.");
            return Ok(new
            {
                contract = "bd2-1", success = true, applied = result.Applied,
                trip = new { result.Trip.Id, result.Trip.Name, Notes = RichNotes.Normalize(result.Trip.Notes), result.Trip.UpdatedAt, result.Trip.ContributionPolicy, result.Trip.Credits }
            });
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }

    /// <summary>Issues a Name/Notes permit for a currently owned Trip using an active incoming credential.</summary>
    [HttpPost("{tripId:guid}/conditional/operations")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> IssueMetadataOperation(Guid tripId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.IssueAsync(tripId, token), token);

    /// <summary>Observes the submitted bearer operation without reading the current Trip.</summary>
    [HttpGet("{tripId:guid}/conditional/operations/{operationId:guid}")]
    [ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public Task<IActionResult> LookupMetadataOperation(Guid tripId, Guid operationId, CancellationToken token) =>
        WithMetadataAuthorityAsync(routes => routes.LookupAsync(tripId, operationId, token), token);

    /// <summary>Uses the same incoming credential resolution and final revalidation as the bearer PATCH.</summary>
    private async Task<IActionResult> WithMetadataAuthorityAsync(Func<TripMetadataReceiptRoutes, Task<IActionResult>> dispatch, CancellationToken token)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, token);
        if (user == null) return Unauthorized();
        return await dispatch(new(Request, _dbContext, user.Id, false,
            (_, cancellation) => authority.RevalidateMutationAsync(HttpContext, user.Id, cancellation)));
    }
}
