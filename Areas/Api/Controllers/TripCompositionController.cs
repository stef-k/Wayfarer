using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Explicit construction input; an omitted own-planning choice retains it. No retry/request key exists.</summary>
public sealed record TripConstructionRequest(Guid SourceA, Guid? SourceB,
    [StringLength(200, MinimumLength = 1)] string? Name,
    [StringLength(2000)] string? ChangeNote, bool KeepOwnBasePlanning = true);

/// <summary>Cookie/antiforgery lifecycle foundation for the later My Trips interface.</summary>
[ApiController, Area("Api"), Authorize(Roles = "User"), Route("api/trip-compositions")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TripCompositionController(ApplicationDbContext db) : ControllerBase
{
    /// <summary>Creates a private independent owned version without a comparison workspace.</summary>
    [HttpPost("versions"), ValidateAntiForgeryToken, RequestSizeLimit(8192)]
    public Task<IActionResult> Version(TripConstructionRequest request, CancellationToken token) => Execute(async owner =>
        await new TripCompositionService(db).CreateAsync(owner, request.SourceA, null, request.Name, request.ChangeNote, request.KeepOwnBasePlanning, token: token));

    /// <summary>Explicitly creates a new private C and immutable authorized A/B comparison.</summary>
    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(8192)]
    public Task<IActionResult> Start(TripConstructionRequest request, CancellationToken token) => Execute(async owner =>
    {
        if (!request.SourceB.HasValue) throw new TripMetadataMutationRejected(400, "composition-source-required", "Select a second source.");
        return await new TripCompositionService(db).CreateAsync(owner, request.SourceA, request.SourceB, request.Name, request.ChangeNote, request.KeepOwnBasePlanning, token: token);
    });

    /// <summary>Lists unfinished comparisons, optionally by the ordered source pair.</summary>
    [HttpGet]
    public Task<IActionResult> List(Guid? sourceA, Guid? sourceB, CancellationToken token) => Execute(owner => new TripCompositionService(db).ListAsync(owner, sourceA, sourceB, token));

    /// <summary>Resumes the same C with its current content and captured comparison inputs.</summary>
    [HttpGet("{id:guid}")]
    public Task<IActionResult> Read(Guid id, CancellationToken token) => Execute(owner => new TripCompositionService(db).ReadAsync(owner, id, token));

    /// <summary>Returns bounded captured-source suggestions or manual current-C search; never confirms a match.</summary>
    [HttpGet("{id:guid}/candidates")]
    public Task<IActionResult> Candidates(Guid id, string source, string kind, Guid sourceId, string? search, CancellationToken token) => Execute(owner =>
        new TripCompositionService(db).CandidatesAsync(owner, id, source, kind, sourceId, search, token: token));

    /// <summary>Saves selected content, placement, dependencies, decisions and credits in one transaction.</summary>
    [HttpPost("{id:guid}/actions"), ValidateAntiForgeryToken, RequestSizeLimit(10485760)]
    public Task<IActionResult> Apply(Guid id, [FromBody] System.Text.Json.JsonElement request,
        [FromServices] PlaceRegionLifecycleService lifecycle, [FromServices] IWebHostEnvironment environment,
        [FromServices] IIconColorProvider colors, [FromServices] SegmentAggregateTokenService routeTokens,
        [FromServices] SegmentRouteClearConfirmation routeConsent, [FromServices] ITripMapThumbnailGenerator thumbnails,
        [FromServices] ICacheWarmupScheduler warmup, CancellationToken token) => Execute(owner =>
        TripCompositionInput.ApplyWithFreshnessAsync(new TripCompositionService(db).ApplyAsync(owner, id, TripCompositionInput.Parse(request), lifecycle, routeTokens, routeConsent,
            TripCompositionInput.Icons(environment), TripCompositionInput.Colors(colors), token: token), thumbnails, warmup));

    /// <summary>Closes comparison and removes its payload without altering C.</summary>
    [HttpPost("{id:guid}/finish"), ValidateAntiForgeryToken]
    public Task<IActionResult> Finish(Guid id, CancellationToken token) => Execute(async owner => await new TripCompositionService(db).FinishAsync(owner, id, token: token));

    /// <summary>Uses ordinary private responses; infrastructure failures never assert that a save rolled back.</summary>
    private async Task<IActionResult> Execute(Func<string, Task<object>> action)
    {
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (owner == null) return Unauthorized();
        try { return Ok(await action(owner)); }
        catch (TripMetadataMutationRejected error) { return error.ToActionResult(); }
        catch (Wayfarer.Models.Dtos.Editor.RegionDeletionRejectedException error) { return StatusCode(error.Status, new { code = error.Code, detail = error.Message }); }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        { return BadRequest(new { code = "composition-request-invalid", detail = "Review the selected fields, dependencies and originals." }); }
        catch (Exception error) when (error is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException or TimeoutException or AggregateException
            || error is InvalidOperationException { InnerException: System.Data.Common.DbException })
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

    /// <summary>Bearer Start uses the same owned-base and current public-reuse authority.</summary>
    [HttpPost("compositions"), RequestSizeLimit(8192)]
    public Task<IActionResult> StartComposition(TripConstructionRequest request, CancellationToken token) => CompositionRequest(async (owner, check) =>
    {
        if (!request.SourceB.HasValue) throw new TripMetadataMutationRejected(400, "composition-source-required", "Select a second source.");
        return await new TripCompositionService(_dbContext).CreateAsync(owner, request.SourceA, request.SourceB, request.Name, request.ChangeNote, request.KeepOwnBasePlanning, check, token);
    }, token);

    /// <summary>Lists this active bearer owner's unfinished comparisons.</summary>
    [HttpGet("compositions")]
    public Task<IActionResult> ListCompositions(Guid? sourceA, Guid? sourceB, CancellationToken token) => CompositionRequest(
        (owner, check) => new TripCompositionService(_dbContext).ListAsync(owner, sourceA, sourceB, token, check), token);

    /// <summary>Reads the saved comparison and independently editable current C.</summary>
    [HttpGet("compositions/{id:guid}")]
    public Task<IActionResult> ReadComposition(Guid id, CancellationToken token) => CompositionRequest(
        (owner, check) => new TripCompositionService(_dbContext).ReadAsync(owner, id, token, check), token);

    /// <summary>Bearer suggestions use immutable inputs and only current owned destination counterparts.</summary>
    [HttpGet("compositions/{id:guid}/candidates")]
    public Task<IActionResult> CompositionCandidates(Guid id, string source, string kind, Guid sourceId, string? search, CancellationToken token) => CompositionRequest(
        (owner, check) => new TripCompositionService(_dbContext).CandidatesAsync(owner, id, source, kind, sourceId, search, check, token), token);

    /// <summary>Bearer selected actions revalidate the incoming credential after all domain waits.</summary>
    [HttpPost("compositions/{id:guid}/actions"), RequestSizeLimit(10485760)]
    public Task<IActionResult> ApplyComposition(Guid id, [FromBody] System.Text.Json.JsonElement request,
        [FromServices] IWebHostEnvironment environment, [FromServices] IIconColorProvider colors, [FromServices] SegmentAggregateTokenService routeTokens,
        [FromServices] SegmentRouteClearConfirmation routeConsent, [FromServices] ITripMapThumbnailGenerator thumbnails,
        [FromServices] ICacheWarmupScheduler warmup, CancellationToken token) => CompositionRequest(
        (owner, check) => TripCompositionInput.ApplyWithFreshnessAsync(new TripCompositionService(_dbContext).ApplyAsync(owner, id, TripCompositionInput.Parse(request), _lifecycle, routeTokens, routeConsent,
            TripCompositionInput.Icons(environment), TripCompositionInput.Colors(colors), check, token), thumbnails, warmup), token);

    /// <summary>Revalidates bearer authority and atomically finishes the owned comparison.</summary>
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
        catch (Wayfarer.Models.Dtos.Editor.RegionDeletionRejectedException error) { return StatusCode(error.Status, new { code = error.Code, detail = error.Message }); }
        catch (Exception error) when (error is System.Text.Json.JsonException or FormatException or KeyNotFoundException)
        { return BadRequest(new { code = "composition-request-invalid", detail = "Review the selected fields, dependencies and originals." }); }
        catch (Exception error) when (error is System.Data.Common.DbException or Microsoft.EntityFrameworkCore.DbUpdateException or TimeoutException or AggregateException
            || error is InvalidOperationException { InnerException: System.Data.Common.DbException })
        {
            return StatusCode(503, new { code = "composition-save-unconfirmed", detail = "The request could not be confirmed. Inspect or refresh your Trips before trying again." });
        }
    }
}
