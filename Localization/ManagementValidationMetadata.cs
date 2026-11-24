using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Wayfarer.Areas.Admin.Models;

namespace Wayfarer.Localization;

/// <summary>Localizes typed catalog input errors without changing values or binder culture.</summary>
internal sealed class ManagementValidationMetadata(WebMessages text) : IBindingMetadataProvider
{
    /// <summary>Limits catalog annotation localization to the two managed catalog forms.</summary>
    internal static bool OwnsCatalog(Type? type) =>
        type == typeof(Models.ActivityType) || type == typeof(TransportProfileCreateViewModel)
        || type == typeof(TransportProfileEditViewModel);

    /// <summary>Supplies locale-aware messages for fields bound from the existing catalog forms.</summary>
    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        if (!OwnsCatalog(context.Key.ContainerType) || context.BindingMetadata.ModelBindingMessageProvider is not { } provider)
            return;

        provider.SetValueMustNotBeNullAccessor(_ => text["Catalogs.Validation.Binding"]);
        provider.SetAttemptedValueIsInvalidAccessor((_, _) => text["Catalogs.Validation.Binding"]);
        provider.SetUnknownValueIsInvalidAccessor(_ => text["Catalogs.Validation.Binding"]);
        provider.SetValueIsInvalidAccessor(_ => text["Catalogs.Validation.Binding"]);
        provider.SetValueMustBeANumberAccessor(_ => text["Catalogs.Validation.Binding"]);
    }
}
