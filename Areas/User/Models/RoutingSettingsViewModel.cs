using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Wayfarer.Areas.User.RoutingModels;

/// <summary>Contains only safe current-user routing settings.</summary>
public sealed class RoutingSettingsViewModel
{
    /// <summary>Gets whether administrators currently permit external routing capability.</summary>
    public bool FeatureEnabled { get; set; }
    /// <summary>Gets or sets the selected approved template; null means server default.</summary>
    public Guid? SelectedProviderConfigurationId { get; set; }
    /// <summary>Gets or sets an optional replacement credential. Blank preserves the stored value.</summary>
    [BindNever]
    public string? Credential { get; set; }
    /// <summary>Gets whether a protected credential is present.</summary>
    public bool CredentialPresent { get; set; }
    /// <summary>Gets the safe current status.</summary>
    public string Status { get; set; } = "Ready";
    /// <summary>Gets or sets the expected concurrency token.</summary>
    public uint RowVersion { get; set; }
    /// <summary>Gets the approved templates.</summary>
    public IReadOnlyList<RoutingTemplateViewModel> Templates { get; set; } = [];
}

/// <summary>Contains one safe administrator-approved template option.</summary>
public sealed record RoutingTemplateViewModel(Guid Id, string DisplayName, bool CredentialRequired, string Disclosure);
