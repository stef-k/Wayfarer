using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Wayfarer.Services;

/// <summary>Translates lookup overload independently of endpoint-specific invalid-token envelopes.</summary>
public sealed class ApiTokenAdmissionFilter : IAsyncActionFilter
{
    /// <summary>Preserves normal results; prevents caught lookup overload becoming an endpoint 500/401.</summary>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var executed = await next();
        var status = IncomingApiTokenResolver.GetDeniedStatus(context.HttpContext);
        if (status == 0) return;
        executed.ExceptionHandled = true;
        context.HttpContext.Response.Headers.RetryAfter = "5";
        executed.Result = context.Controller is Wayfarer.Areas.Api.Controllers.LocationMutationController
            // Only protected commands use the protocol's problem media type; legacy envelopes retain their format.
            ? new ObjectResult(new { code = status == 429 ? "mutationBusy" : "mutationOutcomeUnknown", message = "API token lookup capacity exceeded." })
                { StatusCode = status, ContentTypes = { "application/problem+json" } }
            : new ObjectResult(new { message = "API token lookup capacity exceeded." }) { StatusCode = status };
    }
}
