namespace Wayfarer.Services;

/// <summary>Applies Wayfarer's tile-specific HTTP version and transport safety settings.</summary>
internal static class TileHttpTransportConfiguration
{
    /// <summary>Prefers HTTP/2 while permitting an HTTP/1.1 provider fallback.</summary>
    internal static void Configure(HttpClient client)
    {
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
    }

    /// <summary>Creates the redirect-safe handler with a non-authoritative transport ceiling.</summary>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        MaxConnectionsPerServer = 16
    };
}
