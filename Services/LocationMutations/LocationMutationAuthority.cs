using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

namespace Wayfarer.Services.LocationMutations;

/// <summary>Fresh current-owner authority for protected writes and synchronized future-exclusion/absence evidence.</summary>
internal static class LocationMutationAuthority
{
    /// <summary>Rechecks active account and selected cookie stamp or incoming token under the caller's account fence.</summary>
    internal static async Task<bool> RevalidateAsync(HttpContext context, ApplicationDbContext db, string owner, CancellationToken token)
    {
        var account = await db.Users.FromSqlInterpolated($$"""
            SELECT * FROM "AspNetUsers" WHERE "Id" = {{owner}} FOR SHARE
            """).AsNoTracking().SingleOrDefaultAsync(token);
        if (account is not { IsActive: true }) return false;
        if (context.User.Identity?.IsAuthenticated != true)
            return !ActiveAccountCookie.HasCookieAuthority(context)
                && await IncomingApiTokenResolver.ForRequest(context, db).RevalidateMutationAsync(context, owner, token);
        var stamp = context.User.FindFirstValue("AspNet.Identity.SecurityStamp");
        return !string.IsNullOrEmpty(stamp) && stamp == account.SecurityStamp;
    }
}
