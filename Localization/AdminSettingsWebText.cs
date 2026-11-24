using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Wayfarer.Models;

namespace Wayfarer.Localization;

/// <summary>Localizes Admin Settings input errors and fixed tile-provider presentation.</summary>
internal sealed class AdminSettingsWebText(WebMessages text) : IBindingMetadataProvider
{
    /// <summary>Localizes binder feedback without changing numeric form syntax.</summary>
    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        if (context.Key.ContainerType != typeof(ApplicationSettings) ||
            context.BindingMetadata.ModelBindingMessageProvider is not { } provider)
            return;

        provider.SetValueMustNotBeNullAccessor(_ => text["AdminSettings.Validation.Binding"]);
        provider.SetAttemptedValueIsInvalidAccessor((_, _) => text["AdminSettings.Validation.Binding"]);
        provider.SetUnknownValueIsInvalidAccessor(_ => text["AdminSettings.Validation.Binding"]);
        provider.SetValueIsInvalidAccessor(_ => text["AdminSettings.Validation.Binding"]);
        provider.SetValueMustBeANumberAccessor(_ => text["AdminSettings.Validation.Binding"]);
    }

    /// <summary>Maps legacy template diagnostics without changing the validator or echoing the supplied URL.</summary>
    internal static string TemplateError(WebMessages text, string error) => error switch
    {
        "Tile URL template is required." => text["AdminSettings.Validation.TemplateRequired"],
        "Tile URL template must include {z}, {x}, and {y} placeholders." =>
            text["AdminSettings.Validation.TemplateCoordinates", "{z}", "{x}", "{y}"],
        "Tile URL template must be a valid absolute URL." => text["AdminSettings.Validation.TemplateAbsolute"],
        "Tile URL template must use HTTPS." => text["AdminSettings.Validation.TemplateHttps"],
        "Tile URL template must not include user information." => text["AdminSettings.Validation.TemplateUserInfo"],
        "Credential query parameters must use the {apiKey} placeholder." =>
            text["AdminSettings.Validation.TemplateCredential", "{apiKey}"],
        "Tile URL template must point to a .png resource." => text["AdminSettings.Validation.TemplatePng"],
        _ => text["AdminSettings.Validation.TemplateInvalid"]
    };

    /// <summary>Renders a known compatibility reason and uses a recovery message for unknown reasons.</summary>
    internal static string CompatibilityReason(WebMessages text, string reason) => text[reason switch
    {
        "This removed Thunderforest provider is incompatible with Wayfarer's cache/proxy architecture." => "AdminSettings.PolicyRemovedThunderforest",
        "This removed CARTO provider is not available through Wayfarer's cache/proxy architecture." => "AdminSettings.PolicyRemovedCarto",
        "The endpoint is blocked for compatibility." => "AdminSettings.PolicyEndpointBlocked",
        "The provider endpoint is blank or malformed." => "AdminSettings.PolicyEndpointMalformed",
        "The selected built-in provider endpoint does not match its maintained preset." => "AdminSettings.PolicyPresetMismatch",
        "The provider selection is removed or unknown." => "AdminSettings.PolicyProviderUnknown",
        _ => "AdminSettings.PolicyIncompatible"
    }];

    /// <summary>Translates fixed Wayfarer labels and retains external source URLs exactly.</summary>
    internal static string CompatibilitySource(WebMessages text, string source) => source switch
    {
        "Administrator-managed provider agreement" => text["AdminSettings.PolicySourceAgreement"],
        "Wayfarer compatibility validation" => text["AdminSettings.PolicySourceValidation"],
        _ => source
    };

    /// <summary>Maps cross-field policy errors; individual scalar ranges have their own field validation.</summary>
    internal static string PolicyError(WebMessages text, string error) => text[error switch
    {
        "Burst capacity must be at least maximum concurrency." => "AdminSettings.Validation.BurstConcurrency",
        "Fallback delay cap must not be below the base delay." => "AdminSettings.Validation.FallbackDelayOrder",
        "Total retry ceiling must not be below maximum individual wait." => "AdminSettings.Validation.RetryCeilingOrder",
        _ => "AdminSettings.Validation.PolicyInvalid"
    }];
}
