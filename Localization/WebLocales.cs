using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Localization;

namespace Wayfarer.Localization;

/// <summary>A selectable locale and its catalog plural forms, in selector order.</summary>
public sealed record WebLocale(string Tag, string NativeName, bool PrimaryDefault, string[] PluralCategories,
    Dictionary<string, string> IntegerPluralRules)
{
    /// <summary>Applies the ordered cardinal rules to an invariant, nonnegative integer count.</summary>
    public string PluralCategory(int count)
    {
        var digits = Math.Abs((long)count).ToString(CultureInfo.InvariantCulture);
        foreach (var (category, pattern) in IntegerPluralRules)
            if (Regex.IsMatch(digits, pattern, RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
                return category;

        return "other";
    }
}

/// <summary>Resolves the Web document locale while leaving machine binding on the host culture.</summary>
public sealed class WebLocales : RequestCultureProvider
{
    /// <summary>The neutral resource language and final negotiation fallback.</summary>
    public const string English = "en-US";

    /// <summary>The application preference cookie used by the framework.</summary>
    public static readonly string CookieName = CookieRequestCultureProvider.DefaultCookieName;

    // RFC 9110 §12.4.2 permits at most three fractional digits and only zeros after 1.
    private static readonly Regex LanguageRange = new(
        @"\A[ \t]*([a-zA-Z]{1,8}(?:-[a-zA-Z0-9]{1,8})*)(?:[ \t]*;[ \t]*[qQ]=(0(?:\.[0-9]{0,3})?|1(?:\.0{0,3})?))?[ \t]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    private readonly CultureInfo _bindingCulture = CultureInfo.CurrentCulture;
    private readonly Dictionary<string, WebLocale> _registered;
    private readonly Dictionary<string, WebLocale> _defaults;

    /// <summary>Creates the lookup tables from the application locale registration.</summary>
    public WebLocales(IEnumerable<WebLocale> locales)
    {
        All = locales.ToArray();
        _registered = All.ToDictionary(locale => locale.Tag, StringComparer.OrdinalIgnoreCase);
        _defaults = All.Where(locale => locale.PrimaryDefault)
            .ToDictionary(locale => Primary(locale.Tag), StringComparer.OrdinalIgnoreCase);
        if (Find(English)?.Tag != English || All.Any(locale => CultureInfo.GetCultureInfo(locale.Tag).Name != locale.Tag))
            throw new InvalidOperationException("Web locale registration must include en-US and use canonical tags.");
    }

    /// <summary>The registered choices in display order.</summary>
    public IReadOnlyList<WebLocale> All { get; }

    /// <summary>Finds a registered tag, ignoring HTTP language-tag casing.</summary>
    public WebLocale? Find(string tag) => _registered.GetValueOrDefault(tag);

    /// <summary>Installs the single app provider and limits ambient culture to the binding culture.</summary>
    public void Configure(RequestLocalizationOptions options)
    {
        options.DefaultRequestCulture = new RequestCulture(_bindingCulture, CultureInfo.GetCultureInfo(English));
        options.SupportedCultures = [_bindingCulture];
        options.SupportedUICultures = All.Select(locale => CultureInfo.GetCultureInfo(locale.Tag)).ToArray();
        options.RequestCultureProviders = [this];
    }

    /// <summary>Chooses the cookie, then the first supported positive-quality range.</summary>
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        var request = httpContext.Request;
        var cookie = request.Cookies[CookieName];
        var parsed = cookie is null ? null : CookieRequestCultureProvider.ParseCookieValue(cookie);
        if (parsed is { Cultures.Count: 1, UICultures.Count: 1 }
            && Find(parsed.Cultures[0].ToString()) is { } cookieLocale
            && cookie is not null
            && cookie.Equals($"c={parsed.Cultures[0]}|uic={parsed.UICultures[0]}", StringComparison.Ordinal)
            && Find(parsed.UICultures[0].ToString()) == cookieLocale)
            return Result(cookieLocale);

        // Evaluate each comma-separated member independently, including across multiple header lines.
        var ranges = request.Headers.AcceptLanguage.SelectMany(value => (value ?? "").Split(','))
            .Select(value => LanguageRange.Match(value)).Where(range => range.Success)
            .Select(range => (Tag: range.Groups[1].Value,
                Quality: range.Groups[2].Success ? decimal.Parse(range.Groups[2].Value, CultureInfo.InvariantCulture) : 1m));
        foreach (var (tag, quality) in ranges.OrderByDescending(range => range.Quality))
        {
            if (quality is 0)
                continue;
            if ((Find(tag) ?? _defaults.GetValueOrDefault(Primary(tag))) is { } locale)
                return Result(locale);
        }

        return Result(_registered[English]);
    }

    private Task<ProviderCultureResult?> Result(WebLocale locale) =>
        Task.FromResult<ProviderCultureResult?>(new(_bindingCulture.Name, locale.Tag));

    private static string Primary(string tag) => tag.Split('-', 2)[0];
}
