namespace Wayfarer.Services.Helpers;

/// <summary>
/// Provides upstream download URLs for MBTiles and PBF files based on region names.
/// These can later be extended or loaded from DB/config.
/// </summary>
public static class RegionSourceResolver
{
    public static string GetMbtilesUrl(string region)
    {
        // TODO: replace with real upstream provider
        return $"https://osm-upstream.example.com/mbtiles/{region}.mbtiles";
    }

    public static string GetPbfUrl(string region)
    {
        // TODO: replace with real upstream provider (e.g., Geofabrik)
        return $"https://download.geofabrik.de/europe/{region}-latest.osm.pbf";
    }
}