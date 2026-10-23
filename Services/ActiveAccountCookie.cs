using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Current-account admission at the application cookie's existing validation boundary.</summary>
public static class ActiveAccountCookie
{
    private static readonly object RejectedKey = new();

    /// <summary>Shared navigation/fetch guidance; clients retain their drafts rather than redirecting during a write.</summary>
    public const string SessionEndedMessage = "Your session has ended or your account is disabled. Sign in again. Keep any unsaved changes before leaving this page.";

    /// <summary>Identifies a rejected website session without assigning bearer authority.</summary>
    public static bool WasRejected(HttpContext context) => context.Items.ContainsKey(RejectedKey);

    /// <summary>A presented application cookie keeps priority on mixed routes, including invalid or expired tickets.</summary>
    public static bool HasCookieAuthority(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true || WasRejected(context)) return true;
        var options = context.RequestServices?.GetService<IOptionsMonitor<CookieAuthenticationOptions>>()
            ?.Get(IdentityConstants.ApplicationScheme);
        return options?.Cookie.Name is { } name && context.Request.Cookies.ContainsKey(name);
    }

    /// <summary>Wraps Identity's stamp validator and redirect conventions without changing token-only routes.</summary>
    public static void Configure(CookieAuthenticationOptions options)
    {
        var validate = options.Events.OnValidatePrincipal;
        var login = options.Events.OnRedirectToLogin;
        var denied = options.Events.OnRedirectToAccessDenied;
        options.Events.OnValidatePrincipal = async context =>
        {
            var services = context.HttpContext.RequestServices;
            var identity = services.GetRequiredService<IOptions<IdentityOptions>>().Value.ClaimsIdentity;
            var id = context.Principal?.FindFirst(identity.UserIdClaimType)?.Value;
            var stamp = context.Principal?.FindFirst(identity.SecurityStampClaimType)?.Value;
            var db = services.GetRequiredService<ApplicationDbContext>();
            // A projection bypasses tracked account state. No transaction or account lock spans protected execution.
            var account = await db.Users.AsNoTracking().Where(user => user.Id == id)
                .Select(user => new { user.IsActive, user.SecurityStamp })
                .SingleOrDefaultAsync(context.HttpContext.RequestAborted);
            if (account is { IsActive: true } && !string.IsNullOrEmpty(stamp)
                && string.Equals(stamp, account.SecurityStamp, StringComparison.Ordinal))
            {
                // Retain normal Identity periodic validation, principal refresh and renewal behavior.
                await validate(context);
                if (context.Principal != null) return;
            }

            context.HttpContext.Items[RejectedKey] = true;
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        };
        options.Events.OnRedirectToLogin = context => RedirectAsync(context, login, 401);
        options.Events.OnRedirectToAccessDenied = context => RedirectAsync(context, denied, 403);
    }

    /// <summary>Returns a visible login notice for navigation and a non-success JSON response for fetch callers.</summary>
    private static Task RedirectAsync(RedirectContext<CookieAuthenticationOptions> context,
        Func<RedirectContext<CookieAuthenticationOptions>, Task> original, int status)
    {
        var request = context.Request;
        var fetchMode = request.Headers["Sec-Fetch-Mode"].ToString();
        var fetch = request.Path.StartsWithSegments("/api")
            || request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            || request.Headers["X-Requested-With"] == "XMLHttpRequest"
            || fetchMode is "cors" or "same-origin" or "no-cors";
        if (fetch)
        {
            context.Response.StatusCode = WasRejected(context.HttpContext) ? 401 : status;
            return context.Response.WriteAsJsonAsync(new
            {
                status = context.Response.StatusCode,
                message = context.Response.StatusCode == 401 ? SessionEndedMessage : "You do not have permission to access this resource."
            });
        }
        if (WasRejected(context.HttpContext))
        {
            // Manual owner checks can return Forbid; both paths must lead an ended session to fresh login.
            var login = QueryHelpers.AddQueryString(request.PathBase + context.Options.LoginPath, new Dictionary<string, string?>
            {
                ["sessionEnded"] = "true",
                [context.Options.ReturnUrlParameter] = request.PathBase + request.Path + request.QueryString
            });
            context.Response.Redirect(login);
            return Task.CompletedTask;
        }
        return original(context);
    }
}
