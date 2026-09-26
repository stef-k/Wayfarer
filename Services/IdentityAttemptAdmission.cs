using System.Globalization;
using System.Net;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Wayfarer.Services;

/// <summary>
/// Process-local Identity attempt budget shared across accounts and handlers. A full store
/// rejects new clients instead of evicting live budgets. Only forwarded-middleware IPs are used.
/// </summary>
public sealed class IdentityAttemptAdmission(TimeProvider clock) : IAsyncResourceFilter
{
    public const int AttemptLimit = 20;
    public const int ClientLimit = 4096;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);
    private readonly object _gate = new();
    private readonly Dictionary<IPAddress, (DateTimeOffset End, int Count)> _clients = new();

    /// <summary>Returns zero on admission, otherwise whole seconds until a useful retry.</summary>
    public int Admit(IPAddress? address)
    {
        if (address is null) return (int)Window.TotalSeconds;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        lock (_gate)
        {
            var now = clock.GetUtcNow();
            // Bounded scan also removes idle clients without timers or background ownership.
            foreach (var key in _clients.Where(entry => entry.Value.End <= now).Select(entry => entry.Key).ToArray())
                _clients.Remove(key);
            if (_clients.TryGetValue(address, out var bucket))
            {
                if (bucket.Count >= AttemptLimit) return RetrySeconds(bucket.End - now);
                _clients[address] = (bucket.End, bucket.Count + 1);
                return 0;
            }
            if (_clients.Count >= ClientLimit)
                return RetrySeconds(_clients.Values.Min(value => value.End) - now);
            _clients.Add(address, (now + Window, 1));
            return 0;
        }
    }

    /// <summary>Runs after authorization/antiforgery, before model binding and page execution.</summary>
    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var request = context.HttpContext.Request;
        if (context.ActionDescriptor is PageActionDescriptor { AreaName: "Identity" } page && IsAttempt(page.ViewEnginePath, request))
        {
            var retry = Admit(context.HttpContext.Connection.RemoteIpAddress);
            if (retry > 0)
            {
                context.HttpContext.Response.Headers.RetryAfter = retry.ToString(CultureInfo.InvariantCulture);
                context.Result = new StatusCodeResult(StatusCodes.Status429TooManyRequests);
                return;
            }
        }
        await next();
    }

    /// <summary>Matches page identity, including named-handler fallback, rather than URL spelling.</summary>
    private static bool IsAttempt(string page, HttpRequest request)
    {
        if (HttpMethods.IsPost(request.Method))
            return page is "/Account/Login" or "/Account/Register" or "/Account/LoginWith2fa"
                or "/Account/LoginWithRecoveryCode" or "/Account/ForgotPassword"
                or "/Account/ResetPassword" or "/Account/ResendEmailConfirmation";
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method)) return false;
        // Empty values bind as null and take the framework's non-processing path.
        bool Supplied(string name) => !string.IsNullOrWhiteSpace(request.Query[name].FirstOrDefault());
        return page switch
        {
            "/Account/ConfirmEmail" => Supplied("userId") && Supplied("code"),
            "/Account/ConfirmEmailChange" => Supplied("userId") && Supplied("email") && Supplied("code"),
            "/Account/RegisterConfirmation" => Supplied("email"),
            _ => false
        };
    }

    /// <summary>Round up to avoid advising a retry before the current window ends.</summary>
    private static int RetrySeconds(TimeSpan remaining) => Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds));
}
