using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Localization;

/// <summary>Localizes MVC validation for the private Location, Visit and transfer forms.</summary>
internal sealed class LocationVisitValidationMetadata(WebMessages text) : IBindingMetadataProvider
{
    /// <summary>Limits these binding messages to the form models that use them.</summary>
    internal static bool Owns(Type? type) =>
        type == typeof(AddLocationViewModel) || type == typeof(BulkEditNotesViewModel) || type == typeof(VisitEditViewModel)
        || type == typeof(LocationImportUploadViewModel);

    /// <summary>Supplies display messages without changing form parsing or echoing rejected input.</summary>
    public void CreateBindingMetadata(BindingMetadataProviderContext context)
    {
        if (!Owns(context.Key.ContainerType) || context.BindingMetadata.ModelBindingMessageProvider is not { } provider)
            return;

        if (context.Key.ContainerType == typeof(LocationImportUploadViewModel))
        {
            var key = context.Key.Name == nameof(LocationImportUploadViewModel.FileType)
                ? "Transfers.InvalidFormat" : "Transfers.InvalidUploadValue";
            provider.SetAttemptedValueIsInvalidAccessor((_, _) => text[key]);
            provider.SetUnknownValueIsInvalidAccessor(_ => text[key]);
            provider.SetValueIsInvalidAccessor(_ => text[key]);
            return;
        }

        provider.SetMissingBindRequiredValueAccessor(_ => text["Locations.Required"]);
        provider.SetValueMustNotBeNullAccessor(_ => text["Locations.Required"]);
        provider.SetAttemptedValueIsInvalidAccessor((_, _) => text["Locations.BindingInvalid"]);
        provider.SetUnknownValueIsInvalidAccessor(_ => text["Locations.BindingInvalid"]);
        provider.SetValueIsInvalidAccessor(_ => text["Locations.BindingInvalid"]);
        provider.SetValueMustBeANumberAccessor(_ => text["Locations.BindingInvalid"]);
    }
}
