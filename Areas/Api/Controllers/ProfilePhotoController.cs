using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
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
    // No action parameters: MVC must not create form value providers before our admission.
    public Task<IActionResult> Upload() => ExecuteAsync(async () =>
    {
        if (!await AntiforgeryAsync()) return BadRequest(new { code = "invalid_photo_request" });
        return Ok(await uploads.UploadAsync(Request, Owner, HttpContext.RequestAborted));
    });

    /// <summary>Accepted removal rotates the state token even during absence.</summary>
    [HttpDelete, RequestSizeLimit(4096)]
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
        catch (ProfilePhotoException error)
        {
            if (error.Status != 408) return StatusCode(error.Status, new { code = error.Code });
            // Complete the writable timeout response, then stop the incoming stream
            // instead of letting the host drain a still-progressing upload body.
            Response.StatusCode = 408;
            await Response.WriteAsJsonAsync(new { code = error.Code }, CancellationToken.None);
            await Response.CompleteAsync();
            if (HttpContext.Features.Get<IHttpResetFeature>() is { } reset) reset.Reset(8);
            else HttpContext.Abort();
            return new EmptyResult();
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { return new EmptyResult(); }
        catch (BadHttpRequestException error) when (error.StatusCode == 413) { return StatusCode(413, new { code = "photo_too_large" }); }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException or InvalidOperationException)
        { return StatusCode(503, new { code = "photo_storage_unavailable" }); }
    }
}

/// <summary>Explicit original owner state is required for every removal, including an absent photo.</summary>
public sealed record RemovePhotoRequest(string? ExpectedStateToken);
