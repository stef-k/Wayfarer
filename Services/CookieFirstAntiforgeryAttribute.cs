using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Wayfarer.Areas.Api.Controllers;

namespace Wayfarer.Services;

/// <summary>
/// Validates cookie-selected mutations even when a bearer header is also present.
/// Only a successfully resolved bearer without cookie authority can proceed token-free.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class CookieFirstAntiforgeryAttribute : Attribute, IAsyncActionFilter
{
    /// <summary>Preserves cookie priority and the Location API's invalid-credential envelope.</summary>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var http = context.HttpContext;
        if (http.User.Identity?.IsAuthenticated == true)
        {
            // Validate even if the cookie's account has since become inactive or disappeared.
            var antiforgery = http.RequestServices.GetRequiredService<IAntiforgery>();
            if (!await antiforgery.IsRequestValidAsync(http))
            {
                context.Result = new AntiforgeryValidationFailedResult();
                return;
            }
        }
        else
        {
            var user = ((BaseApiController)context.Controller).GetUserFromTokenOrCookie();
            if (user == null)
            {
                context.Result = new UnauthorizedObjectResult("Invalid or missing API token.");
                return;
            }
        }

        // The action keeps ownership checks; bearer resolution is request-cached. Lookup overload
        // remains inside the existing outer ApiTokenAdmissionFilter's 429/503 result boundary.
        await next();
    }
}
