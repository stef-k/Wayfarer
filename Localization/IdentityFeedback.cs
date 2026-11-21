using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wayfarer.Services;

namespace Wayfarer.Localization;

/// <summary>Translates the pinned Identity UI's string-only outcomes at the Web presentation boundary.</summary>
public sealed class IdentityFeedback(WebMessages messages) : IResultFilter
{
    /// <summary>Identifies the Identity page independently of its URL aliases.</summary>
    internal static bool IsIdentityPage(Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor? action) =>
        action is PageActionDescriptor { AreaName: "Identity" };

    // These values are carried across redirects by the pinned application and package handlers.
    private static readonly Dictionary<string, (string Key, bool Failed)> StatusMessages = new(StringComparer.Ordinal)
    {
        ["Thank you for confirming your email."] = ("Identity.Email.Confirmed", false),
        ["Error confirming your email."] = ("Identity.Email.ConfirmFailed", true),
        ["Thank you for confirming your email change."] = ("Identity.Email.ChangeConfirmed", false),
        ["Error changing email."] = ("Identity.Email.ChangeFailed", true),
        ["Error changing user name."] = ("Identity.Email.UsernameChangeFailed", true),
        ["Confirmation link to change email sent. Please check your email."] = ("Identity.Email.ChangeSent", false),
        ["Your email is unchanged."] = ("Identity.Email.Unchanged", false),
        ["Verification email sent. Please check your email."] = ("Identity.Email.VerificationSent", false),
        ["Your password has been changed."] = ("Identity.Password.Changed", false),
        ["Your password has been set."] = ("Identity.Password.SetComplete", false),
        ["Your profile has been updated"] = ("Identity.Manage.Updated", false),
        ["The profile changed or could not be saved. Refresh and save your preferences again."] =
            ("Identity.Manage.Conflict", true),
        ["Your authenticator app has been verified."] = ("Identity.Authenticator.Verified", false),
        ["Your authenticator app key has been reset, you will need to configure your authenticator app using the new key."] =
            ("Identity.Authenticator.KeyReset", false),
        ["2fa has been disabled. You can reenable 2fa when you setup an authenticator app"] =
            ("Identity.TwoFactor.Disabled", false),
        ["The current browser has been forgotten. When you login again from this browser you will be prompted for your 2fa code."] =
            ("Identity.TwoFactor.BrowserForgotten", false),
        ["You have generated new recovery codes."] = ("Identity.Recovery.Generated", false),
        ["The external login was not removed."] = ("Identity.External.NotRemoved", true),
        ["The external login was removed."] = ("Identity.External.Removed", false),
        ["The external login was not added. External logins can only be associated with one account."] =
            ("Identity.External.NotAdded", true),
        ["The external login was added."] = ("Identity.External.Added", false),
    };

    /// <summary>Renders a status without rewriting the value carried in TempData.</summary>
    public static (string Message, bool Failed) Status(WebMessages messages, string status) =>
        StatusMessages.TryGetValue(status, out var entry)
            ? (messages[entry.Key], entry.Failed)
            : (messages["Identity.Error.GenericStatus"], true);

    /// <summary>Translates visible messages after the handlers have made all decisions.</summary>
    public void OnResultExecuting(ResultExecutingContext context)
    {
        if (!IsIdentityPage(context.ActionDescriptor))
            return;

        if (context.Result is PageResult)
        {
            foreach (var entry in context.ModelState.Values)
                TranslateErrors(entry.Errors);
        }
        else if (context.Result is ObjectResult { Value: string body } result && TranslateBody(body) is { } translation)
        {
            result.Value = translation;
            context.HttpContext.Response.Headers.ContentLanguage = messages.Locale.Tag;
        }
    }

    /// <summary>Leaves the emitted response to the normal Razor Pages result pipeline.</summary>
    public void OnResultExecuted(ResultExecutedContext context) { }

    /// <summary>Preserves the page's field keys, attempted values and validation state.</summary>
    private void TranslateErrors(ModelErrorCollection errors)
    {
        for (var i = 0; i < errors.Count; i++)
        {
            if (TranslateError(errors[i].ErrorMessage) is { } translated)
                errors[i] = new ModelError(translated);
        }
    }

    /// <summary>Recognizes application and package messages, keeping provider text as a display argument.</summary>
    private string? TranslateError(string error)
    {
        const string externalPrefix = "Error from external provider: ";
        return error switch
        {
            "Invalid login attempt." => messages["Identity.Login.Invalid"],
            "Your account is inactive. Please contact support." => messages["Identity.Login.Inactive"],
            ActiveAccountCookie.SessionEndedMessage => messages["Identity.Login.SessionEnded"],
            "UserName is already taken." => messages["Identity.Register.UsernameTaken"],
            "Invalid authenticator code." => messages["Identity.TwoFactor.InvalidCode"],
            "Invalid recovery code entered." => messages["Identity.TwoFactor.InvalidRecovery"],
            "Verification code is invalid." => messages["Identity.Authenticator.Invalid"],
            "Incorrect password." => messages["Identity.Password.Incorrect"],
            "Verification email sent. Please check your email." => messages["Identity.Email.VerificationSent"],
            "Error loading external login information." => messages["Identity.External.LoadError"],
            "Error loading external login information during confirmation." => messages["Identity.External.ConfirmError"],
            _ when error.StartsWith(externalPrefix, StringComparison.Ordinal) =>
                messages["Identity.External.ProviderError", error[externalPrefix.Length..]],
            _ => null
        };
    }

    /// <summary>Translates known status bodies, including the existing diagnostic argument when present.</summary>
    private string? TranslateBody(string body)
    {
        const string idPrefix = "Unable to load user with ID '";
        const string emailPrefix = "Unable to load user with email '";
        const string suffix = "'.";
        return body switch
        {
            "A code must be supplied for password reset." => messages["Identity.Password.MissingCode"],
            _ when body.StartsWith(idPrefix, StringComparison.Ordinal) && body.EndsWith(suffix, StringComparison.Ordinal) =>
                messages["Identity.Error.UserId", body[idPrefix.Length..^suffix.Length]],
            _ when body.StartsWith(emailPrefix, StringComparison.Ordinal) && body.EndsWith(suffix, StringComparison.Ordinal) =>
                messages["Identity.Error.UserEmail", body[emailPrefix.Length..^suffix.Length]],
            _ => null
        };
    }
}
