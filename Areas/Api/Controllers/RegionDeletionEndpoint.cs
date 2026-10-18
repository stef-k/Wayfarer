using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.Dtos.Editor;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Thin shared JSON/ProblemDetails adapter for the two explicitly distinct authority boundaries.</summary>
internal static class RegionDeletionEndpoint
{
    /// <summary>Checks ownership before parsing and invokes the same lifecycle implementation for both adapters.</summary>
    internal static async Task<IActionResult> ExecuteAsync(ControllerBase controller, ApplicationDbContext db,
        PlaceRegionLifecycleService lifecycle, Guid tripId, Guid regionId, string userId, bool mutation, CancellationToken token)
    {
        controller.Response.Headers.CacheControl = "no-store";
        if (!await db.Regions.AsNoTracking().AnyAsync(r => r.Id == regionId && r.TripId == tripId
            && r.UserId == userId && r.Trip.UserId == userId, token)) return controller.NotFound();
        try
        {
            var request = await RegionDeletionRequestParser.ParseAsync(controller.Request.Body, mutation, token);
            return mutation
                ? controller.Ok(await lifecycle.ConfirmRegionDeletionAsync(tripId, regionId, userId, request, token))
                : controller.Ok(await lifecycle.PreviewRegionDeletionAsync(tripId, regionId, userId, request, token));
        }
        catch (RegionDeletionRejectedException rejection)
        {
            ProblemDetails problem = rejection.Status == 400
                ? new ValidationProblemDetails(new Dictionary<string, string[]> { ["request"] = [rejection.Message] })
                : new ProblemDetails();
            problem.Status = rejection.Status;
            problem.Title = rejection.Message;
            problem.Type = $"https://wayfarer/errors/{rejection.Code}";
            problem.Extensions["code"] = rejection.Code;
            if (rejection.Preview != null) problem.Extensions["preview"] = rejection.Preview;
            return new ObjectResult(problem) { StatusCode = rejection.Status, ContentTypes = { "application/problem+json" } };
        }
    }
}
