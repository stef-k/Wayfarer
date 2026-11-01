using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;
using Wayfarer.Util;
using Wayfarer.Middleware;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Cookie-only owner management. Antiforgery headers are validated before any multipart body access.</summary>
[Area("Api"), ApiController, Route("api/profile-photo/me")]
[Authorize(AuthenticationSchemes = "Identity.Application")]
public sealed class ProfilePhotoController(ProfilePhotoService photos, ProfilePhotoUpload uploads, IAntiforgery antiforgery) : ControllerBase
{
    /// <summary>Private recovery state; no group or public projection carries its concurrency token.</summary>
    [HttpGet]
    public Task<IActionResult> State(CancellationToken ct) => ExecuteAsync(async () =>
    {
        var state = await photos.OwnerStateAsync(Owner, ct);
        return state == null ? NotFound() : Ok(state);
    });

    /// <summary>Stream after authorization, header antiforgery and no-queue operation admission.</summary>
    [HttpPost, UserFileUpload, RequestSizeLimit(16 * 1024 * 1024)]
    public Task<IActionResult> Upload(CancellationToken ct) => ExecuteAsync(async () =>
    {
        if (!await AntiforgeryAsync()) return BadRequest(new { code = "invalid_photo_request" });
        return Ok(await uploads.UploadAsync(Request, Owner, ct));
    });

    /// <summary>Accepted removal rotates the state token even during absence.</summary>
    [HttpDelete]
    public Task<IActionResult> Remove([FromBody] RemovePhotoRequest request, CancellationToken ct) => ExecuteAsync(async () =>
    {
        if (!await AntiforgeryAsync()) return BadRequest(new { code = "invalid_photo_request" });
        return Ok(await photos.MutateAsync(Owner, request.ExpectedStateToken ?? "", null, ct));
    });

    private string Owner => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    /// <summary>An absent header cannot fall through to antiforgery's multipart form-token reader.</summary>
    private async Task<bool> AntiforgeryAsync()
    {
        if (string.IsNullOrEmpty(Request.Headers["RequestVerificationToken"])) return false;
        try { await antiforgery.ValidateRequestAsync(HttpContext); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }

    /// <summary>Return only fixed contract errors; uncertain commit is distinct from definite failure.</summary>
    private async Task<IActionResult> ExecuteAsync(Func<Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "private, no-store";
        try { return await action(); }
        catch (ProfilePhotoException error) { return StatusCode(error.Status, new { code = error.Code }); }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return StatusCode(413, new { code = "photo_too_large" }); }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException or InvalidOperationException)
        { return StatusCode(503, new { code = "photo_storage_unavailable" }); }
    }
}

/// <summary>Explicit original owner state is required for every removal, including an absent photo.</summary>
public sealed record RemovePhotoRequest(string? ExpectedStateToken);
