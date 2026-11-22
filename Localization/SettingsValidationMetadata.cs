using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Wayfarer.Areas.User.LocationProviderModels;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Localization;

/// <summary>Localizes binding errors in settings forms while retaining the existing binding culture.</summary>
internal sealed class SettingsValidationMetadata(WebMessages text) : IBindingMetadataProvider
{
    /// <summary>Limits localization to the settings input models that own catalog keys.</summary>
    internal static bool Owns(Type? type) =>
        type == typeof(TimelineSettingsViewModel) || type == typeof(HiddenAreaCreateViewModel)
        || type == typeof(HiddenAreaEditViewModel) || type == typeof(LocationProviderCredentialInput)
        || type == typeof(LocationProviderProfileInput) || type == typeof(LocationProviderChoiceInput)
        || type == typeof(LocationProviderGuardInput) || type == typeof(MapboxPermanentConsentInput);

    /// <summary>Displays localized binder errors without reflecting a private input value back to the page.</summary>
    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        if (!Owns(context.Key.ContainerType) || context.BindingMetadata.ModelBindingMessageProvider is not { } provider)
            return;

        provider.SetValueMustNotBeNullAccessor(_ => text["Settings.BindingInvalid"]);
        provider.SetAttemptedValueIsInvalidAccessor((_, _) => text["Settings.BindingInvalid"]);
        provider.SetUnknownValueIsInvalidAccessor(_ => text["Settings.BindingInvalid"]);
        provider.SetValueIsInvalidAccessor(_ => text["Settings.BindingInvalid"]);
    }
}
