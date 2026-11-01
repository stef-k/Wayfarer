using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Thin cookie, bearer and anonymous adapters share one persisted read policy and private storage owner.</summary>
[Area("Api"), ApiController]
public sealed class ProfilePhotoReadsController(ProfilePhotoService photos, IMobileCurrentUserAccessor mobile) : ControllerBase
{
    /// <summary>Only the active cookie owner can read their exact current bytes.</summary>
    [HttpGet("/api/profile-photo/me/{version}"), Authorize(AuthenticationSchemes = "Identity.Application")]
    public Task<IActionResult> Owner(string version, CancellationToken ct) => ReadAsync(version,
        parsed => photos.OwnerVersionAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, parsed, ct));

    /// <summary>Cookie authority is confined to this exact Group.</summary>
    [HttpGet("/api/groups/{groupId:guid}/members/{userId}/photo/{version}"), Authorize(AuthenticationSchemes = "Identity.Application")]
    public Task<IActionResult> Group(Guid groupId, string userId, string version, CancellationToken ct) => ReadAsync(version,
        parsed => photos.GroupVersionAsync(User.FindFirstValue(ClaimTypes.NameIdentifier)!, groupId, userId, parsed, ct));

    /// <summary>The established incoming-token resolver is the only bearer authentication authority.</summary>
    [HttpGet("/api/mobile/groups/{groupId:guid}/members/{userId}/photo/{version}")]
    public async Task<IActionResult> MobileGroup(Guid groupId, string userId, string version, CancellationToken ct)
    {
        var user = await mobile.GetCurrentUserAsync(ct);
        if (user is not { IsActive: true }) return Unauthorized();
        return await ReadAsync(version, parsed => photos.GroupVersionAsync(user.Id, groupId, userId, parsed, ct));
    }

    /// <summary>Cookies and bearer tokens cannot bypass exact public-Trip visibility and account consent.</summary>
    [AllowAnonymous, HttpGet("/Public/Trips/{tripId:guid}/owner-photo/{version}")]
    public Task<IActionResult> Public(Guid tripId, string version, CancellationToken ct) => ReadAsync(version,
        parsed => photos.PublicVersionAsync(tripId, parsed, ct));

    /// <summary>Conditional headers never bypass policy; no ETag, last-modified or static redirect is issued.</summary>
    private async Task<IActionResult> ReadAsync(string version, Func<Guid, Task<Guid?>> authorize)
    {
        if (!Guid.TryParseExact(version, "N", out var parsed)) return NotFound();
        try
        {
            if (await authorize(parsed) == null) return NotFound();
            var bytes = photos.Read(parsed);
            return bytes == null ? NotFound() : File(bytes, "image/webp");
        }
        catch (System.Data.Common.DbException) { return StatusCode(503); }
    }
}
