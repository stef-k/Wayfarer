using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Wayfarer.Areas.Identity.Pages.Account;

namespace Wayfarer.Localization;

/// <summary>Supplies request-localized field labels and messages to standard MVC Identity validators.</summary>
internal sealed class IdentityValidationMetadata(WebMessages messages)
    : IDisplayMetadataProvider, IValidationMetadataProvider, IBindingMetadataProvider
{
    /// <summary>Limits the annotation adapter to the local and pinned Identity UI model types.</summary>
    internal static bool Owns(Type? type) => type == typeof(RegisterModel.InputModel) ||
        type?.Namespace?.StartsWith("Wayfarer.Areas.Identity.Pages.", StringComparison.Ordinal) == true ||
        type?.Namespace?.StartsWith("Microsoft.AspNetCore.Identity.UI.V5.Pages.", StringComparison.Ordinal) == true;

    /// <summary>Keeps labels request-local when the same metadata is reused across locales.</summary>
    public void CreateDisplayMetadata(DisplayMetadataProviderContext context)
    {
        var key = FieldKey(context.Key);
        if (key is not null)
            context.DisplayMetadata.DisplayName = () => messages[key];
    }

    /// <summary>Copies Identity validation rules with explicit message keys, including implicit required rules.</summary>
    public void CreateValidationMetadata(ValidationMetadataProviderContext context)
    {
        if (FieldKey(context.Key) is null)
            return;

        var attributes = context.ValidationMetadata.ValidatorMetadata;
        for (var i = 0; i < attributes.Count; i++)
        {
            if (LocalizedRule(attributes[i]) is { } localized)
                attributes[i] = localized;
        }
    }

    /// <summary>Localizes binding messages for Identity fields without changing the values MVC accepts.</summary>
    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        var key = FieldKey(context.Key);
        if (key is null || context.BindingMetadata.ModelBindingMessageProvider is not { } provider)
            return;

        provider.SetValueMustNotBeNullAccessor(_ => messages["Identity.Validation.Required", messages[key]]);
        provider.SetAttemptedValueIsInvalidAccessor((value, _) => messages["Identity.Validation.InvalidValue", value, messages[key]]);
        provider.SetUnknownValueIsInvalidAccessor(_ => messages["Identity.Validation.FieldInvalid", messages[key]]);
        provider.SetValueIsInvalidAccessor(value => messages["Identity.Validation.ValueInvalid", value]);
    }

    /// <summary>Identifies rendered form fields by their existing property names.</summary>
    private static string? FieldKey(ModelMetadataIdentity field)
    {
        if (!Owns(field.ContainerType))
            return null;

        return field.Name switch
        {
            "RememberMe" => "Identity.Login.Remember",
            "Code" when field.ContainerType == typeof(Areas.Identity.Pages.Account.Manage.EnableAuthenticatorModel.InputModel) =>
                "Identity.Field.VerificationCode",
            "Code" when field.ContainerType == typeof(Microsoft.AspNetCore.Identity.UI.V5.Pages.Account.Internal.ResetPasswordModel.InputModel) =>
                "Identity.Field.ResetCode",
            "ConfirmPassword" when field.ContainerType?.GetProperty("NewPassword") is not null =>
                "Identity.Field.ConfirmNewPassword",
            "ShowProfilePhotoOnPublicTrips" => "Identity.Manage.ShowPhoto",
            "Username" or "Password" or "OldPassword" or "NewPassword" or "ConfirmPassword" or "Email"
                or "NewEmail" or "DisplayName" or "RecoveryCode" or "TwoFactorCode" or "RememberMachine" =>
                "Identity.Field." + field.Name,
            _ => null
        };
    }

    /// <summary>Uses MVC's own validation and client adapters with portable message keys.</summary>
    private static ValidationAttribute? LocalizedRule(object attribute) => attribute switch
    {
        RequiredAttribute required => new RequiredAttribute
        {
            AllowEmptyStrings = required.AllowEmptyStrings,
            ErrorMessage = "Identity.Validation.Required"
        },
        StringLengthAttribute length => new StringLengthAttribute(length.MaximumLength)
        {
            MinimumLength = length.MinimumLength,
            ErrorMessage = length.MinimumLength > 0 ? "Identity.Validation.Length" : "Identity.Validation.MaximumLength"
        },
        EmailAddressAttribute => new EmailAddressAttribute { ErrorMessage = "Identity.Validation.Email" },
        CompareAttribute compare => new CompareAttribute(compare.OtherProperty)
        {
            ErrorMessage = compare.OtherProperty == "NewPassword" ?
                "Identity.Validation.NewPasswordMismatch" : "Identity.Validation.PasswordMismatch"
        },
        _ => null
    };

}
