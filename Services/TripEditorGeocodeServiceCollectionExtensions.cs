using Microsoft.Extensions.Options;
using Wayfarer.Models.Options;

namespace Wayfarer.Services;

/// <summary>
/// Registers the Trip Editor geocode search proxy dependencies.
/// </summary>
public static class TripEditorGeocodeServiceCollectionExtensions
{
    private const string DefaultNominatimUserAgent = "Wayfarer/1.0";

    /// <summary>Adds the cache, rate limiter, provider, and search service for Trip Editor geocode search.</summary>
    public static IServiceCollection AddTripEditorGeocodeSearch(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TripEditorGeocodeOptions>(configuration.GetSection("TripEditorGeocode"));
        services.AddSingleton<ITripEditorGeocodeClock, SystemTripEditorGeocodeClock>();
        services.AddSingleton<TripEditorGeocodeRateLimiter>();
        services.AddScoped<ITripEditorGeocodeSearchService, TripEditorGeocodeSearchService>();
        services.AddHttpClient<NominatimTripEditorGeocodeProvider>()
            .ConfigureHttpClient((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<TripEditorGeocodeOptions>>().Value;
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
                var userAgent = string.IsNullOrWhiteSpace(options.NominatimUserAgent)
                    ? DefaultNominatimUserAgent
                    : options.NominatimUserAgent;
                if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent))
                {
                    client.DefaultRequestHeaders.UserAgent.TryParseAdd(DefaultNominatimUserAgent);
                }

                if (!string.IsNullOrWhiteSpace(options.Referer) && Uri.TryCreate(options.Referer, UriKind.Absolute, out var referer))
                {
                    client.DefaultRequestHeaders.Referrer = referer;
                }
            }).RemoveAllLoggers();
        services.AddHttpClient<GeoapifyTripEditorGeocodeProvider>(client =>
            client.Timeout = TimeSpan.FromSeconds(Math.Max(1, configuration.GetValue("TripEditorGeocode:TimeoutSeconds", 5))))
            .RemoveAllLoggers();

        return services;
    }
}
