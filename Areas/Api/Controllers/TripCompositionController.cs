using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Explicit construction input; an omitted own-planning choice retains it. No retry/request key exists.</summary>
public sealed record TripConstructionRequest(Guid SourceA, Guid? SourceB,
    [property: StringLength(200, MinimumLength = 1)] string? Name,
    [property: StringLength(2000)] string? ChangeNote, bool KeepOwnBasePlanning = true);

/// <summary>Cookie/antiforgery lifecycle foundation for the later My Trips interface.</summary>
[ApiController, Area("Api"), Authorize(Roles = "User"), Route("api/trip-compositions")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TripCompositionController(ApplicationDbContext db) : ControllerBase
{
    [HttpPost("versions"), ValidateAntiForgeryToken, RequestSizeLimit(8192)]
    public Task<IActionResult> Version(TripConstructionRequest request, CancellationToken token) => Execute(async owner =>
        await new TripCompositionService(db).CreateAsync(owner, request.SourceA, null, request.Name, request.ChangeNote, request.KeepOwnBasePlanning, token: token));

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(8192)]
    public Task<IActionResult> Start(TripConstructionRequest request, CancellationToken token) => Execute(async owner =>
    {
        if (!request.SourceB.HasValue) throw new TripMetadataMutationRejected(400, "composition-source-required", "Select a second source.");
        return await new TripCompositionService(db).CreateAsync(owner, request.SourceA, request.SourceB, request.Name, request.ChangeNote, request.KeepOwnBasePlanning, token: token);
    });

    [HttpGet]
    public Task<IActionResult> List(Guid? sourceA, Guid? sourceB, CancellationToken token) => Execute(owner => new TripCompositionService(db).ListAsync(owner, sourceA, sourceB, token));

    [HttpGet("{id:guid}")]
    public Task<IActionResult> Read(Guid id, CancellationToken token) => Execute(owner => new TripCompositionService(db).ReadAsync(owner, id, token));

    [HttpPost("{id:guid}/finish"), ValidateAntiForgeryToken]
    public Task<IActionResult> Finish(Guid id, CancellationToken token) => Execute(async owner => await new TripCompositionService(db).FinishAsync(owner, id, token: token));

    /// <summary>Uses ordinary private responses; infrastructure failures never assert that a save rolled back.</summary>
    private async Task<IActionResult> Execute(Func<string, Task<object>> action)
    {
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (owner == null) return Unauthorized();
        try { return Ok(await action(owner)); }
        catch (TripMetadataMutationRejected error) { return error.ToActionResult(); }
        catch (Exception error) when (error is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException or TimeoutException)
        {
            return StatusCode(503, new { code = "composition-save-unconfirmed", detail = "The request could not be confirmed. Inspect or refresh your Trips before trying again." });
        }
    }
}

public partial class TripsController
{
    /// <summary>Bearer construction uses the same service and rechecks the credential after all acquisition waits.</summary>
    [HttpPost("versions"), RequestSizeLimit(8192)]
    public Task<IActionResult> CreateVersion(TripConstructionRequest request, CancellationToken token) => CompositionRequest(async (owner, check) =>
        await new TripCompositionService(_dbContext).CreateAsync(owner, request.SourceA, null, request.Name, request.ChangeNote, request.KeepOwnBasePlanning, check, token), token);

    [HttpPost("compositions"), RequestSizeLimit(8192)]
    public Task<IActionResult> StartComposition(TripConstructionRequest request, CancellationToken token) => CompositionRequest(async (owner, check) =>
    {
        if (!request.SourceB.HasValue) throw new TripMetadataMutationRejected(400, "composition-source-required", "Select a second source.");
        return await new TripCompositionService(_dbContext).CreateAsync(owner, request.SourceA, request.SourceB, request.Name, request.ChangeNote, request.KeepOwnBasePlanning, check, token);
    }, token);

    [HttpGet("compositions")]
    public Task<IActionResult> ListCompositions(Guid? sourceA, Guid? sourceB, CancellationToken token) => CompositionRequest(
        (owner, check) => new TripCompositionService(_dbContext).ListAsync(owner, sourceA, sourceB, token, check), token);

    [HttpGet("compositions/{id:guid}")]
    public Task<IActionResult> ReadComposition(Guid id, CancellationToken token) => CompositionRequest(
        (owner, check) => new TripCompositionService(_dbContext).ReadAsync(owner, id, token, check), token);

    [HttpPost("compositions/{id:guid}/finish")]
    public Task<IActionResult> FinishComposition(Guid id, CancellationToken token) => CompositionRequest(async (owner, check) =>
        await new TripCompositionService(_dbContext).FinishAsync(owner, id, check, token), token);

    /// <summary>Never substitutes cookie identity for incoming bearer authority; every response is private/no-store.</summary>
    private async Task<IActionResult> CompositionRequest(Func<string, Func<CancellationToken, Task<bool>>, Task<object>> action, CancellationToken token)
    {
        Response.Headers.CacheControl = "private, no-store";
        var authority = IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext);
        var user = await authority.ResolveAsync(HttpContext, token);
        if (user == null) return Unauthorized();
        try { return Ok(await action(user.Id, ct => authority.RevalidateMutationAsync(HttpContext, user.Id, ct))); }
        catch (TripMetadataMutationRejected error) { return error.ToActionResult(); }
        catch (Exception error) when (error is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException or TimeoutException)
        {
            return StatusCode(503, new { code = "composition-save-unconfirmed", detail = "The request could not be confirmed. Inspect or refresh your Trips before trying again." });
        }
    }
}
