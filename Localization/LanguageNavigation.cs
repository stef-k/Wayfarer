using Microsoft.AspNetCore.Diagnostics;

namespace Wayfarer.Localization;

/// <summary>Identifies documents that can safely reload after a preference write.</summary>
public static class LanguageNavigation
{
    private static readonly HashSet<string> TemporaryIdentityPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConfirmEmail", "ConfirmEmailChange", "RegisterConfirmation", "ResetPassword", "ResetPasswordConfirmation",
        "ExternalLogin", "LoginWithRecoveryCode", "LoginWith2fa", "GenerateRecoveryCodes", "ShowRecoveryCodes",
        "EnableAuthenticator"
    };

    /// <summary>Returns an application-owned GET or the empty string for a safe current URL.</summary>
    public static string? GetDestination(HttpContext context, string? postDestination)
    {
        var values = context.Request.RouteValues;
        if (context.Features.Get<IStatusCodeReExecuteFeature>() is not null
            || context.Features.Get<IExceptionHandlerFeature>() is not null)
            return null;

        var area = values["area"] as string;
        var controller = values["controller"] as string;
        var action = values["action"] as string;
        var page = values["page"] as string;
        if (IsUnavailable(area, controller, action, page))
            return null;
        return HttpMethods.IsGet(context.Request.Method) ? "" : postDestination;
    }

    private static bool IsUnavailable(string? area, string? controller, string? action, string? page)
    {
        if (page is not null && TemporaryIdentityPages.Contains(page.Split('/')[^1]))
            return true;
        if (!string.Equals(area, "User", StringComparison.OrdinalIgnoreCase))
            return false;

        return controller?.ToUpperInvariant() switch
        {
            "APITOKEN" => true,
            "TRIP" => action?.ToUpperInvariant() is "INDEX" or "COMPOSE" or "COPY" or "IMPORT" or "REVIEWIMPORT",
            "TIMELINE" => action?.ToUpperInvariant() is "INDEX" or "CHRONOLOGICAL",
            "TIMELINECALENDAR" => true,
            "LOCATION" => true,
            _ => false
        };
    }
}
