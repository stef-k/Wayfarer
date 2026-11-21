using System.Xml.Linq;
using Microsoft.Extensions.Configuration;
using Wayfarer.Localization;
using Xunit;

namespace Wayfarer.Tests.Views;

/// <summary>Validates shipped catalog shape and argument contracts in the normal .NET test suite.</summary>
public sealed class WebCatalogTests
{
    /// <summary>Every delivered catalog has exactly its registered plural forms and complete translated entries.</summary>
    [Theory]
    [InlineData("Shell")]
    [InlineData("Editor")]
    [InlineData("Viewer")]
    public void CatalogsHaveMatchingEntriesAndArguments(string owner)
    {
        var locales = WebLocalization.ReadLocales(new ConfigurationBuilder().Build());
        var english = Read(owner, "");
        var englishCategories = locales.Find(WebLocales.English)!.PluralCategories;
        foreach (var locale in locales.All.Where(locale => locale.Tag != WebLocales.English))
        {
            var translated = Read(owner, "." + locale.Tag);
            var required = english.SelectMany(entry => Keys(entry.Key, englishCategories, locale.PluralCategories))
                .ToHashSet(StringComparer.Ordinal);
            Assert.Equal(required.Order(), translated.Keys.Order());
            foreach (var (key, value) in translated)
            {
                var neutralKey = english.ContainsKey(key) ? key : key[..key.LastIndexOf('.')] + ".other";
                Assert.True(WebMessages.HasSameArguments(english[neutralKey], value), $"{locale.Tag}: {key}");
            }
        }
    }

    private static Dictionary<string, string> Read(string owner, string suffix)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "LocalizationCatalogs", owner + suffix + ".resx");
        var nodes = XDocument.Load(path).Root?.Elements("data") ?? [];
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var key = (string?)node.Attribute("name") ?? "";
            var value = node.Element("value")?.Value ?? "";
            Assert.StartsWith(owner + ".", key);
            Assert.False(string.IsNullOrWhiteSpace(value), key);
            Assert.True(WebMessages.HasSameArguments(value, value), key);
            Assert.True(entries.TryAdd(key, value), $"Duplicate key: {key}");
        }
        Assert.NotEmpty(entries);
        return entries;
    }

    private static IEnumerable<string> Keys(string key, string[] english, string[] categories)
    {
        var lastDot = key.LastIndexOf('.');
        if (!english.Contains(key[(lastDot + 1)..]))
            return [key];
        return categories.Select(category => key[..lastDot] + "." + category);
    }
}
