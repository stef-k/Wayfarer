using Microsoft.Extensions.Options;
using Wayfarer.Models.Options;

namespace Wayfarer.Services;

/// <summary>
/// Registers the Trip Editor geocode search proxy dependencies.
/// </summary>
public static class TripEditorGeocodeServiceCollectionExtensions
{
    /// <summary>Adds the cache, rate limiter, provider, and search service for Trip Editor geocode search.</summary>
    public static IServiceCollection AddTripEditorGeocodeSearch(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<TripEditorGeocodeOptions>(configuration.GetSection("TripEditorGeocode"));
        services.AddSingleton<ITripEditorGeocodeClock, SystemTripEditorGeocodeClock>();
        services.AddSingleton<TripEditorGeocodeRateLimiter>();
        services.AddScoped<ITripEditorGeocodeSearchService, TripEditorGeocodeSearchService>();
        services.AddHttpClient<ITripEditorGeocodeProvider, NominatimTripEditorGeocodeProvider>()
            .ConfigureHttpClient((sp, client) =>
            {
                var options = sp.GetRequiredService<IOptions<TripEditorGeocodeOptions>>().Value;
                var contactEmail = sp.GetRequiredService<IConfiguration>().GetSection("Application:ContactEmail").Value ?? "noreply@wayfarer.app";
                client.Timeout = TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds));
                var userAgent = string.IsNullOrWhiteSpace(options.NominatimUserAgent)
                    ? $"Wayfarer/1.0 (contact: {contactEmail})"
                    : options.NominatimUserAgent;
                if (!client.DefaultRequestHeaders.UserAgent.TryParseAdd(userAgent))
                {
                    client.DefaultRequestHeaders.UserAgent.TryParseAdd("Wayfarer/1.0");
                }

                if (!string.IsNullOrWhiteSpace(options.Referer) && Uri.TryCreate(options.Referer, UriKind.Absolute, out var referer))
                {
                    client.DefaultRequestHeaders.Referrer = referer;
                }
            });

        return services;
    }
}
