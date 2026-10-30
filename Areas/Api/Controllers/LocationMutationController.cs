using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.LocationMutations;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>One protected single-record protocol for cookie Web and current owner API authority.</summary>
[Route("api/location/{id:int}/mutations/{baseId:guid}/{actionId:guid}")]
public sealed class LocationMutationController(ApplicationDbContext db, ILogger<BaseApiController> logger,
    IDataProtectionProvider protection, ReverseGeocodingService reverse, IMemoryCache cache) : BaseApiController(db, logger)
{
    /// <summary>Buffers the original body under the existing server limit, then binds its exact bytes once.</summary>
    [HttpPost, CookieFirstAntiforgery(ProtectedMutation = true)]
    public async Task<IActionResult> Execute(int id, Guid baseId, Guid actionId, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var user = GetUserFromTokenOrCookie();
        if (user == null) return ProblemResult("mutationAuthenticationRequired", 401);
        if (Request.Headers.ContentEncoding.Count > 0 && Request.Headers.ContentEncoding != "identity")
            return ProblemResult("mutationInvalid", 422);
        try
        {
            using var buffer = new MemoryStream();
            await Request.Body.CopyToAsync(buffer, token);
            var bytes = buffer.ToArray();
            var command = LocationMutationCommand.Parse(bytes);
            if (id <= 0) return ProblemResult("mutationInvalid", 422);
            if (command.Adapter == "web-edit" && User.Identity?.IsAuthenticated != true)
                return ProblemResult("mutationForbidden", 403);
            var service = Service(user.Id);
            return Reply(await service.ExecuteAsync(user.Id, id, baseId, actionId, command, bytes, token));
        }
        catch (MutationProtocolException failure) { return ProblemResult(failure.Code, 422); }
        catch (Exception failure) when (failure is NpgsqlException or DbUpdateException or OperationCanceledException)
        { return ProblemResult("mutationOutcomeUnknown", 503); }
    }

    /// <summary>Read-only recovery never starts another executor or provider contact.</summary>
    [HttpGet]
    public async Task<IActionResult> Recover(int id, Guid baseId, Guid actionId, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        var user = GetUserFromTokenOrCookie();
        if (user == null) return ProblemResult("mutationAuthenticationRequired", 401);
        try { return Reply(await Service(user.Id).RecoverAsync(user.Id, id, baseId, actionId, Request.Headers["X-Location-Base"], token)); }
        catch (Exception failure) when (failure is NpgsqlException or DbUpdateException or OperationCanceledException)
        { return ProblemResult("mutationOutcomeUnknown", 503); }
    }

    /// <summary>Revalidates active account, cookie stamp or incoming token while holding final transaction locks.</summary>
    private LocationMutationService Service(string owner) => new(_dbContext, protection, reverse, cache)
    {
        Authorized = async token =>
        {
            var account = await _dbContext.Users.FromSqlInterpolated($$"""
                SELECT * FROM "AspNetUsers" WHERE "Id" = {{owner}} FOR SHARE
                """).AsNoTracking().SingleOrDefaultAsync(token);
            if (account is not { IsActive: true }) return false;
            if (User.Identity?.IsAuthenticated != true)
                return await IncomingApiTokenResolver.ForRequest(HttpContext, _dbContext).RevalidateMutationAsync(HttpContext, owner, token);
            var stamp = User.FindFirstValue("AspNet.Identity.SecurityStamp");
            return !string.IsNullOrEmpty(stamp) && stamp == account.SecurityStamp;
        }
    };

    /// <summary>Correlated terminal rejections retain their full envelope and problem media type.</summary>
    private IActionResult Reply(LocationMutationReply reply) => new ObjectResult(reply.Body)
    { StatusCode = reply.Status, ContentTypes = { reply.Status >= 400 ? "application/problem+json" : "application/json" } };

    /// <summary>Unscoped errors cannot be mistaken for terminal action evidence.</summary>
    private IActionResult ProblemResult(string code, int status) => Reply(LocationMutationReceipts.Problem(code, status));
}
