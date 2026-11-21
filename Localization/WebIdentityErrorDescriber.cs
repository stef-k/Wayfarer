using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Wayfarer.Localization;

/// <summary>Localizes user-facing Identity descriptions while preserving framework error codes.</summary>
public sealed class WebIdentityErrorDescriber(IHttpContextAccessor accessor, WebMessages messages) : IdentityErrorDescriber
{
    /// <summary>Restricts translated descriptions to the Razor Identity UI.</summary>
    private IdentityError Describe(IdentityError error, params object?[] arguments)
    {
        if (IdentityFeedback.IsIdentityPage(accessor.HttpContext?.GetEndpoint()?.Metadata.GetMetadata<PageActionDescriptor>()))
            error.Description = messages["Identity.ErrorCode." + error.Code, arguments];
        return error;
    }

    /// <inheritdoc />
    public override IdentityError DefaultError() => Describe(base.DefaultError());

    /// <inheritdoc />
    public override IdentityError ConcurrencyFailure() => Describe(base.ConcurrencyFailure());

    /// <inheritdoc />
    public override IdentityError PasswordMismatch() => Describe(base.PasswordMismatch());

    /// <inheritdoc />
    public override IdentityError InvalidToken() => Describe(base.InvalidToken());

    /// <inheritdoc />
    public override IdentityError RecoveryCodeRedemptionFailed() => Describe(base.RecoveryCodeRedemptionFailed());

    /// <inheritdoc />
    public override IdentityError LoginAlreadyAssociated() => Describe(base.LoginAlreadyAssociated());

    /// <inheritdoc />
    public override IdentityError InvalidUserName(string? userName) => Describe(base.InvalidUserName(userName), userName);

    /// <inheritdoc />
    public override IdentityError InvalidEmail(string? email) => Describe(base.InvalidEmail(email), email);

    /// <inheritdoc />
    public override IdentityError DuplicateUserName(string userName) => Describe(base.DuplicateUserName(userName), userName);

    /// <inheritdoc />
    public override IdentityError DuplicateEmail(string email) => Describe(base.DuplicateEmail(email), email);

    /// <inheritdoc />
    public override IdentityError UserAlreadyHasPassword() => Describe(base.UserAlreadyHasPassword());

    /// <inheritdoc />
    public override IdentityError PasswordTooShort(int length) => Describe(base.PasswordTooShort(length), length);

    /// <inheritdoc />
    public override IdentityError PasswordRequiresUniqueChars(int uniqueChars) => Describe(base.PasswordRequiresUniqueChars(uniqueChars), uniqueChars);

    /// <inheritdoc />
    public override IdentityError PasswordRequiresNonAlphanumeric() => Describe(base.PasswordRequiresNonAlphanumeric());

    /// <inheritdoc />
    public override IdentityError PasswordRequiresDigit() => Describe(base.PasswordRequiresDigit());

    /// <inheritdoc />
    public override IdentityError PasswordRequiresLower() => Describe(base.PasswordRequiresLower());

    /// <inheritdoc />
    public override IdentityError PasswordRequiresUpper() => Describe(base.PasswordRequiresUpper());
}
