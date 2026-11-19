using Microsoft.Net.Http.Headers;

namespace Wayfarer.Localization;

/// <summary>Registers locale configuration and applies cache policy to negotiated Web bodies.</summary>
public static class WebLocalization
{
    /// <summary>Registers request localization, MVC resources and the Web catalog reader.</summary>
    public static void AddWebLocalization(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(ReadLocales(configuration));
        services.AddSingleton<WebMessages>();
        services.AddLocalization(options => options.ResourcesPath = "Resources");
        services.AddOptions<RequestLocalizationOptions>()
            .Configure<WebLocales>((options, locales) => locales.Configure(options));
    }

    /// <summary>Reads bundled registration with an optional deployment configuration override.</summary>
    internal static WebLocales ReadLocales(IConfiguration configuration)
    {
        using var file = typeof(WebLocales).Assembly.GetManifestResourceStream("Wayfarer.Resources.WebLocales.json")
            ?? throw new InvalidOperationException("Web locale registration is missing.");
        var bundled = new ConfigurationBuilder().AddJsonStream(file).Build();
        var configured = configuration.GetSection("WebLocales").Get<WebLocale[]>();
        return new WebLocales(configured ?? bundled.GetSection("WebLocales").Get<WebLocale[]>() ?? []);
    }

    /// <summary>Resolves one locale and marks HTML documents and fragments private.</summary>
    public static void UseWebLocalization(this IApplicationBuilder app)
    {
        app.UseRequestLocalization();
        app.Use(async (context, next) =>
        {
            var locale = System.Globalization.CultureInfo.CurrentUICulture.Name;
            context.Response.OnStarting(() =>
            {
                var response = context.Response;
                if (response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                {
                    response.Headers.ContentLanguage = locale;
                    var cache = response.GetTypedHeaders().CacheControl ?? new CacheControlHeaderValue();
                    cache.Private = true;
                    cache.Public = false;
                    cache.NoCache = true;
                    response.GetTypedHeaders().CacheControl = cache;
                    response.Headers.AppendCommaSeparatedValues(HeaderNames.Vary, HeaderNames.AcceptLanguage, HeaderNames.Cookie);
                }
                return Task.CompletedTask;
            });
            await next();
        });
    }
}
