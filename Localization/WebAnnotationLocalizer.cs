using Microsoft.Extensions.Localization;

namespace Wayfarer.Localization;

/// <summary>Adapts existing Web catalog keys to MVC's server and unobtrusive validation adapters.</summary>
internal sealed class WebAnnotationLocalizer(WebMessages messages, params string[] prefixes) : IStringLocalizer
{
    /// <inheritdoc />
    public LocalizedString this[string name] => this[name, []];

    /// <inheritdoc />
    public LocalizedString this[string name, params object[] arguments] =>
        prefixes.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) ?
            new(name, messages[name, arguments]) : new(name, name, resourceNotFound: true);

    /// <inheritdoc />
    public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
}
