using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.DataAnnotations;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Net.Http.Headers;
using Wayfarer.Models.ViewModels;

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
        services.AddOptions<MvcOptions>().Configure<WebMessages>((options, messages) =>
            {
            options.ModelMetadataDetailsProviders.Add(new SettingsValidationMetadata(messages));
            options.ModelMetadataDetailsProviders.Add(new LocationVisitValidationMetadata(messages));
            options.ModelMetadataDetailsProviders.Add(new ManagementValidationMetadata(messages));
        });
        services.AddOptions<MvcDataAnnotationsLocalizationOptions>()
            .PostConfigure<WebMessages>((options, messages) =>
            {
                var previous = options.DataAnnotationLocalizerProvider;
                var settings = new WebAnnotationLocalizer(messages, "Settings.", "HiddenAreas.", "Providers.Validation.");
                var locations = new WebAnnotationLocalizer(messages, "Locations.", "Transfers.");
                var trips = new WebAnnotationLocalizer(messages, "Editor.Trips");
                var accounts = new WebAnnotationLocalizer(messages, "Accounts.Validation.");
                var catalogs = new WebAnnotationLocalizer(messages, "Catalogs.Validation.");
                options.DataAnnotationLocalizerProvider = (type, factory) =>
                    SettingsValidationMetadata.Owns(type) ? settings
                    : LocationVisitValidationMetadata.Owns(type) ? locations
                    : type == typeof(Wayfarer.Models.Trip) ? trips
                    : type == typeof(CreateUserViewModel) || type == typeof(EditUserViewModel) || type == typeof(ChangePasswordViewModel) ? accounts
                    : ManagementValidationMetadata.OwnsCatalog(type) ? catalogs
                    : previous?.Invoke(type, factory) ?? factory.Create(type);
            });
    }

    /// <summary>Connects the pinned Identity UI to the Web catalog through MVC presentation hooks.</summary>
    internal static void AddIdentityPresentation(this IServiceCollection services)
    {
        // Translate the pinned Identity UI's string outcomes after handler execution.
        services.AddScoped<IdentityFeedback>();
        services.Configure<RazorPagesOptions>(options =>
            options.Conventions.AddAreaFolderApplicationModelConvention("Identity", "/", page =>
                page.Filters.Add(new ServiceFilterAttribute(typeof(IdentityFeedback)))));

        // Keep the standard MVC client and server validation rules and localize Identity metadata.
        services.AddOptions<MvcOptions>().Configure<WebMessages>((options, messages) =>
            options.ModelMetadataDetailsProviders.Add(new IdentityValidationMetadata(messages)));
        services.AddOptions<MvcDataAnnotationsLocalizationOptions>()
            .PostConfigure<WebMessages>((options, messages) =>
            {
                var defaultProvider = options.DataAnnotationLocalizerProvider;
                var identity = new WebAnnotationLocalizer(messages, "Identity.Validation.");
                options.DataAnnotationLocalizerProvider = (type, factory) =>
                    IdentityValidationMetadata.Owns(type) ? identity : defaultProvider?.Invoke(type, factory) ?? factory.Create(type);
            });
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
