using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Wayfarer.Localization;

/// <summary>Reads Web presentation catalogs with explicit English fallback and bounded diagnostics.</summary>
public sealed partial class WebMessages
{
    private static readonly Assembly ApplicationAssembly = typeof(WebMessages).Assembly;
    private static readonly ConcurrentDictionary<(Assembly, string), ResourceManager> Resources = new();
    private readonly WebLocales _locales;
    private readonly ILogger<WebMessages> _logger;
    private readonly Assembly _localizedResources;

    /// <summary>Uses the application assembly's catalogs and language registration.</summary>
    public WebMessages(WebLocales locales, ILogger<WebMessages> logger) : this(locales, logger, ApplicationAssembly) { }

    /// <summary>Allows the test host to load its own translation satellite alongside built-in English.</summary>
    internal WebMessages(WebLocales locales, ILogger<WebMessages> logger, Assembly localizedResources)
    {
        _locales = locales;
        _logger = logger;
        _localizedResources = localizedResources;
    }
    private readonly ConcurrentDictionary<(string Locale, string Key), byte> _reported = new();

    /// <summary>The active document locale used for lookups and display formatting.</summary>
    public WebLocale Locale => _locales.Find(CultureInfo.CurrentUICulture.Name) ?? _locales.Find(WebLocales.English)
        ?? throw new InvalidOperationException("English locale registration is missing.");

    /// <summary>Reads one translated phrase and substitutes positional display arguments.</summary>
    public string this[string key, params object?[] args]
    {
        get
        {
            var (text, _) = Resolve(Locale.Tag, key);
            return args.Length == 0 ? text : string.Format(CultureInfo.GetCultureInfo(Locale.Tag), text, args);
        }
    }

    /// <summary>The active culture for display values, separate from request binding.</summary>
    public CultureInfo DisplayCulture => CultureInfo.GetCultureInfo(Locale.Tag);

    /// <summary>Formats a display number without changing its corresponding wire value.</summary>
    public string DisplayNumber(int count) => count.ToString("N0", DisplayCulture);

    /// <summary>Looks up a whole count message, using English grammar for missing translations. Argument zero is the formatted count.</summary>
    public string Count(string key, int count, params object?[] args)
    {
        var (text, translated) = Resolve(Locale.Tag, $"{key}.{Locale.PluralCategory(count)}");
        if (!translated)
        {
            var english = _locales.Find(WebLocales.English)
                ?? throw new InvalidOperationException("English locale registration is missing.");
            (text, _) = Resolve(english.Tag, $"{key}.{english.PluralCategory(count)}");
        }
        return string.Format(DisplayCulture, text, [DisplayNumber(count), ..args]);
    }

    /// <summary>Serializes the requested feature catalogs with English companions for client fallback.</summary>
    public string Bootstrap(params string[] owners)
    {
        var active = new Dictionary<string, string>(StringComparer.Ordinal);
        var english = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var owner in owners.Distinct(StringComparer.Ordinal))
        {
            AddMessages(owner, english, active);
        }
        // The default encoder escapes HTML-significant characters, including a script end tag.
        return JsonSerializer.Serialize(new { locale = Locale.Tag, fallback = WebLocales.English, messages = active, english });
    }

    private void AddMessages(string owner, Dictionary<string, string> english, Dictionary<string, string> active)
    {
        var catalog = Catalog(owner);
        var neutral = catalog.GetResourceSet(CultureInfo.InvariantCulture, true, false);
        var localized = Catalog(owner, _localizedResources).GetResourceSet(DisplayCulture, true, false);
        if (neutral is null)
            return;

        foreach (System.Collections.DictionaryEntry entry in neutral)
        {
            if (entry.Key is not string key || entry.Value is not string value)
                continue;
            english.Add(key, value);
            var (text, translated) = Resolve(Locale.Tag, key);
            if (translated)
                active.Add(key, text);
        }

        if (localized is null)
            return;

        // A language can have extra plural forms absent from the English resource set.
        foreach (System.Collections.DictionaryEntry entry in localized)
        {
            if (entry.Key is not string key || active.ContainsKey(key) || english.ContainsKey(key))
                continue;
            var (text, translated) = Resolve(Locale.Tag, key);
            if (translated)
                active.Add(key, text);
        }
    }

    private (string Text, bool Translated) Resolve(string locale, string key)
    {
        var owner = key.Split('.', 2)[0];
        var resources = Catalog(owner);
        var neutral = resources.GetResourceSet(CultureInfo.InvariantCulture, true, false);
        var english = neutral?.GetString(key);
        var dot = key.LastIndexOf('.');
        if (english is null && dot >= 0 && _locales.Find(locale)?.PluralCategories.Contains(key[(dot + 1)..]) == true)
            english = neutral?.GetString(key[..dot] + ".other");
        if (english is null)
        {
            Report(locale, key);
            return (key, false);
        }
        if (locale == WebLocales.English)
            return (english, true);

        var value = Catalog(owner, _localizedResources).GetResourceSet(CultureInfo.GetCultureInfo(locale), true, false)?.GetString(key);
        if (!string.IsNullOrWhiteSpace(value) && HasSameArguments(english, value))
            return (value, true);

        Report(locale, key);
        return (english, false);
    }

    private void Report(string locale, string key)
    {
        lock (_reported)
        {
            if (_reported.Count < 200 && _reported.TryAdd((locale, key), 0))
                _logger.LogWarning("Missing or invalid Web message {MessageKey} for locale {Locale}", key, locale);
        }
    }

    private static ResourceManager Catalog(string owner, Assembly? assembly = null) =>
        Resources.GetOrAdd((assembly ?? ApplicationAssembly, owner),
            entry => new ResourceManager($"{entry.Item1.GetName().Name}.Resources.{entry.Item2}", entry.Item1));

    /// <summary>Checks positional argument parity and rejects unsupported brace expressions.</summary>
    internal static bool HasSameArguments(string english, string translation)
    {
        var englishArgs = Argument().Matches(english).Select(match => match.Value).Order();
        var translatedArgs = Argument().Matches(translation).Select(match => match.Value).Order();
        var literal = Argument().Replace(translation, "");
        return !literal.Contains('{') && !literal.Contains('}') && englishArgs.SequenceEqual(translatedArgs);
    }

    [GeneratedRegex(@"\{[0-9]+\}")]
    private static partial Regex Argument();
}
