namespace Wayfarer.Models.Options;

/// <summary>
/// Configuration for the Trip Editor geocode search proxy.
/// </summary>
public sealed class TripEditorGeocodeOptions
{
    /// <summary>Public Nominatim search endpoint.</summary>
    public string NominatimSearchEndpoint { get; set; } = "https://nominatim.openstreetmap.org/search";

    /// <summary>Application-identifying User-Agent sent to Nominatim.</summary>
    public string NominatimUserAgent { get; set; } = "Wayfarer/1.0 (contact: noreply@wayfarer.app)";

    /// <summary>Optional referer header sent to Nominatim when configured.</summary>
    public string? Referer { get; set; }

    /// <summary>Cache lifetime for normalized identical search queries.</summary>
    public int CacheSeconds { get; set; } = 60;

    /// <summary>Provider HTTP timeout in seconds.</summary>
    public int TimeoutSeconds { get; set; } = 5;

    /// <summary>Minimum interval between uncached provider requests.</summary>
    public int MinimumIntervalMilliseconds { get; set; } = 1000;
}
