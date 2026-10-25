using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>Bearer-only nonmutating content preview, including Places with no dependencies.</summary>
    [HttpPost("{tripId:guid}/conditional/places/{placeId:guid}/deletion-preview")]
    public Task<IActionResult> PreviewPlaceDeletion(Guid tripId, Guid placeId, CancellationToken token) => PlaceDeletionAsync(tripId, placeId, false, token);

    /// <summary>One content-approved bearer deletion with current credential revalidation under lifecycle locks.</summary>
    [HttpPost("{tripId:guid}/conditional/places/{placeId:guid}/deletion")]
    public Task<IActionResult> ConfirmPlaceDeletion(Guid tripId, Guid placeId, CancellationToken token) => PlaceDeletionAsync(tripId, placeId, true, token);

    private async Task<IActionResult> PlaceDeletionAsync(Guid tripId, Guid placeId, bool mutation, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, token);
        return user == null ? Unauthorized() : await PlaceDeletionEndpoint.ExecuteAsync(this, _dbContext, _lifecycle,
            tripId, placeId, user.Id, mutation, token, ct => authority.RevalidateMutationAsync(HttpContext, user.Id, ct));
    }
}

public sealed partial class TripEditorController
{
    /// <summary>Cookie/User-role preview with antiforgery and complete owned scope.</summary>
    [HttpPost("places/{placeId:guid}/deletion-preview")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> PreviewPlaceDeletion(Guid tripId, Guid placeId, [FromServices] PlaceRegionLifecycleService lifecycle, CancellationToken token) =>
        PlaceDeletionAsync(tripId, placeId, lifecycle, false, token);

    /// <summary>Cookie/User-role confirmed deletion shares the bearer lifecycle compare/apply owner.</summary>
    [HttpPost("places/{placeId:guid}/deletion")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ConfirmPlaceDeletion(Guid tripId, Guid placeId, [FromServices] PlaceRegionLifecycleService lifecycle, CancellationToken token) =>
        PlaceDeletionAsync(tripId, placeId, lifecycle, true, token);

    private Task<IActionResult> PlaceDeletionAsync(Guid tripId, Guid placeId, PlaceRegionLifecycleService lifecycle, bool mutation, CancellationToken token)
    {
        var failure = RequireEditorUser(out var userId);
        return failure != null ? Task.FromResult(failure)
            : PlaceDeletionEndpoint.ExecuteAsync(this, _dbContext, lifecycle, tripId, placeId, userId!, mutation, token);
    }
}

/// <summary>Strict shared wire adapter; authority and owned target checks precede parsing or private proof reads.</summary>
internal static class PlaceDeletionEndpoint
{
    /// <summary>Checks concealed ownership before strict parsing, then dispatches only the selected preview or approved operation.</summary>
    internal static async Task<IActionResult> ExecuteAsync(ControllerBase controller, ApplicationDbContext db,
        PlaceRegionLifecycleService lifecycle, Guid tripId, Guid placeId, string userId, bool mutation,
        CancellationToken token, Func<CancellationToken, Task<bool>>? authority = null)
    {
        controller.Response.Headers.CacheControl = "no-store";
        if (!await db.Places.AsNoTracking().AnyAsync(p => p.Id == placeId && p.UserId == userId
            && p.Region.TripId == tripId && p.Region.UserId == userId && p.Region.Trip.UserId == userId, token)) return controller.NotFound();
        try
        {
            var confirmation = await PlaceDeletionRequestParser.ParseAsync(controller.Request.Body, mutation, token);
            return mutation ? controller.Ok(await lifecycle.ConfirmPlaceDeletionAsync(tripId, placeId, userId, confirmation, token, authority))
                : controller.Ok(await lifecycle.PreviewPlaceDeletionAsync(tripId, placeId, userId, token));
        }
        catch (TripMetadataMutationRejected rejection) { return rejection.ToActionResult(); }
    }
}
