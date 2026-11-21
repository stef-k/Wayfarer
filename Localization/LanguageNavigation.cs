using Microsoft.AspNetCore.Diagnostics;

namespace Wayfarer.Localization;

/// <summary>Identifies documents that can safely reload after a preference write.</summary>
public static class LanguageNavigation
{
    private static readonly HashSet<string> TemporaryIdentityPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "ConfirmEmail", "ConfirmEmailChange", "RegisterConfirmation", "ForgotPasswordConfirmation",
        "ResetPassword", "ResetPasswordConfirmation",
        "ExternalLogin", "LoginWithRecoveryCode", "LoginWith2fa", "GenerateRecoveryCodes", "ShowRecoveryCodes",
        "EnableAuthenticator"
    };

    private static readonly HashSet<string> IdentityFormPages = new(StringComparer.OrdinalIgnoreCase)
    {
        "/Account/Login", "/Account/Register", "/Account/ForgotPassword", "/Account/ResendEmailConfirmation",
        "/Account/Manage/Index", "/Account/Manage/Email", "/Account/Manage/ChangePassword",
        "/Account/Manage/SetPassword", "/Account/Manage/DeletePersonalData"
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
        if (HttpMethods.IsGet(context.Request.Method))
            return "";
        return area == "Identity" ? GetIdentityFormDestination(context, page) : postDestination;
    }

    /// <summary>Discards POST handler selectors while retaining return URLs and Login's session-expiry flag.</summary>
    private static string? GetIdentityFormDestination(HttpContext context, string? page)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || page is null || !IdentityFormPages.Contains(page))
            return null;

        var returnUrl = page is "/Account/Login" or "/Account/Register"
            ? context.Request.Query["returnUrl"].FirstOrDefault()
            : null;
        var sessionEnded = page == "/Account/Login" && context.Request.Query["sessionEnded"] == "true"
            ? "true"
            : null;
        return context.RequestServices.GetRequiredService<LinkGenerator>().GetPathByPage(
            context, page, handler: null, values: new { area = "Identity", returnUrl, sessionEnded });
    }

    /// <summary>Withholds switching on transient Identity pages and unintegrated application workspaces.</summary>
    private static bool IsUnavailable(string? area, string? controller, string? action, string? page)
    {
        if (area == "Identity" && page is not null && TemporaryIdentityPages.Contains(page.Split('/')[^1]))
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
