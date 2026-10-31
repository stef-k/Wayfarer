using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Explicit bearer owner snapshot, including private planning even on an owned public Trip.</summary>
    [HttpGet("{id:guid}/planning")]
    public async Task<IActionResult> GetPlanning(Guid id, [FromServices] IDbContextFactory<ApplicationDbContext> contexts,
        [FromServices] IOptions<JsonOptions> jsonOptions)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try { return await ReadTripSnapshotAsync(id, contexts, jsonOptions.Value, budget.Token, ownerPrivate: true); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { return FullReadFailure(); }
    }

    /// <summary>Bearer Set/Clear shares the existing credential revalidation and mutation row barriers.</summary>
    [HttpPatch("{tripId:guid}/planning/{kind}/{targetId:guid}")]
    [RequestSizeLimit(262144)]
    public async Task<IActionResult> PatchPlanning(Guid tripId, string kind, Guid targetId, CancellationToken cancellationToken)
    {
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, cancellationToken);
        if (user == null) return Unauthorized();
        try
        {
            return Ok(await new PlanningMutationService(_dbContext).ApplyAsync(tripId, kind, targetId, user.Id, Request.Body,
                token => authority.RevalidateMutationAsync(HttpContext, user.Id, token), cancellationToken));
        }
        catch (TripMetadataMutationRejected error) { return error.ToActionResult(); }
    }
}

public sealed partial class TripEditorController
{
    /// <summary>Cookie/User/antiforgery planning save; private errors and successes bypass caches.</summary>
    [HttpPatch("planning/{kind}/{targetId:guid}")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(262144)]
    public async Task<IActionResult> PatchPlanning(Guid tripId, string kind, Guid targetId, CancellationToken cancellationToken)
    {
        var failure = RequireEditorUser(out var userId);
        if (failure != null) return failure;
        try
        {
            return Ok(await new PlanningMutationService(_dbContext).ApplyAsync(tripId, kind, targetId, userId!, Request.Body, null, cancellationToken));
        }
        catch (TripMetadataMutationRejected error) { return error.ToActionResult(); }
    }
}
