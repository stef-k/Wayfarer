using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Localization;

/// <summary>Localizes MVC validation for the private Location and Visit forms.</summary>
internal sealed class LocationVisitValidationMetadata(WebMessages text) : IBindingMetadataProvider
{
    /// <summary>Limits these binding messages to the form models that use them.</summary>
    internal static bool Owns(Type? type) =>
        type == typeof(AddLocationViewModel) || type == typeof(BulkEditNotesViewModel) || type == typeof(VisitEditViewModel);

    /// <summary>Supplies display messages without changing form parsing or echoing rejected input.</summary>
    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        if (!Owns(context.Key.ContainerType) || context.BindingMetadata.ModelBindingMessageProvider is not { } provider)
            return;

        provider.SetMissingBindRequiredValueAccessor(_ => text["Locations.Required"]);
        provider.SetValueMustNotBeNullAccessor(_ => text["Locations.Required"]);
        provider.SetAttemptedValueIsInvalidAccessor((_, _) => text["Locations.BindingInvalid"]);
        provider.SetUnknownValueIsInvalidAccessor(_ => text["Locations.BindingInvalid"]);
        provider.SetValueIsInvalidAccessor(_ => text["Locations.BindingInvalid"]);
        provider.SetValueMustBeANumberAccessor(_ => text["Locations.BindingInvalid"]);
    }
}
