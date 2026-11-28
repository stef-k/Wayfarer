using Microsoft.AspNetCore.Mvc.Filters;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Refuses obsolete Web dispatch after authentication and antiforgery and before the domain action.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class RequireTripStructureDispatchAttribute(bool replaced = false) : ActionFilterAttribute
{
    internal const string Header = "X-Wayfarer-Trip-Dispatch";
    internal const string Version = "3";

    /// <summary>Accepts one exact protocol header only on retained dependent routes.</summary>
    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var values = context.HttpContext.Request.Headers[Header];
        if (replaced || values.Count != 1 || values[0] != Version)
            context.Result = new TripMetadataMutationRejected(409, "trip-write-upgrade-required",
                "Reload this page to use protected Trip edits.").ToActionResult();
    }
}
