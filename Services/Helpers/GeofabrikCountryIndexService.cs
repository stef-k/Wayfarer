using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Wayfarer.Services.Helpers;

/// <summary>
/// Dynamically fetches and caches the Geofabrik country index, and resolves OSM PBF URLs.
/// </summary>
public class GeofabrikCountryIndexService
{
    private readonly ILogger<GeofabrikCountryIndexService> _logger;
    private readonly string _indexPath;
    private readonly HttpClient _httpClient;

    private Dictionary<string, string> _countryToPbfUrl = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, (double minLon, double minLat, double maxLon, double maxLat)>
        _countryToEnvelope = new(StringComparer.OrdinalIgnoreCase);

    private bool _isLoaded = false;

    private const string IndexUrl = "https://download.geofabrik.de/index-v1.json";

    public GeofabrikCountryIndexService(ILogger<GeofabrikCountryIndexService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _httpClient = new HttpClient();

        var osmPbfDir = configuration.GetSection("CacheSettings:OsmPbfCache").Value ?? "OsmPbfCache";
        if (!Path.IsPathRooted(osmPbfDir))
        {
            osmPbfDir = Path.Combine(Directory.GetCurrentDirectory(), osmPbfDir);
        }

        if (!Directory.Exists(osmPbfDir))
        {
            Directory.CreateDirectory(osmPbfDir);
        }

        _indexPath = Path.Combine(osmPbfDir, "geofabrik-index.json");
    }

    public bool IsSupported(string countryCode)
    {
        EnsureIndexLoaded();
        return _countryToPbfUrl.ContainsKey(countryCode);
    }

    public string GetPbfUrl(string code)
    {
        EnsureIndexLoaded();

        // OrdinalIgnoreCase dictionary, but try upper just in case
        if (_countryToPbfUrl.TryGetValue(code, out var url) ||
            _countryToPbfUrl.TryGetValue(code.ToUpperInvariant(), out url))
            return url;

        throw new NotSupportedException($"Country '{code}' is not supported.");
    }

    public IEnumerable<string> ListSupportedCountries()
    {
        EnsureIndexLoaded();
        return _countryToPbfUrl.Keys.OrderBy(x => x);
    }

    public void ForceUpdate()
    {
        try
        {
            _logger.LogInformation("Downloading Geofabrik index...");
            var json = _httpClient.GetStringAsync(IndexUrl).Result;
            File.WriteAllText(_indexPath, json);
            _logger.LogInformation("Geofabrik index saved to {Path}", _indexPath);
            _isLoaded = false; // force reload
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Geofabrik index.");
        }
    }

    private void EnsureIndexLoaded()
    {
        if (_isLoaded) return;

        if (!File.Exists(_indexPath) || File.GetLastWriteTimeUtc(_indexPath) < DateTime.UtcNow.AddDays(-1))
        {
            ForceUpdate();
        }

        var json = File.ReadAllText(_indexPath);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        _countryToPbfUrl = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Traverse(root);

        _isLoaded = true;
    }

/* helper stays the same ------------------------------------------- */
    private static (double, double, double, double)? DeriveBbox(JsonElement coords)
    {
        var stack = new Stack<JsonElement>();
        stack.Push(coords);

        double minLon = 180,
            minLat = 90,
            maxLon = -180,
            maxLat = -90;
        bool found = false;

        while (stack.Count > 0)
        {
            var el = stack.Pop();
            if (el.ValueKind != JsonValueKind.Array) continue;

            if (el.GetArrayLength() == 2 &&
                el[0].ValueKind == JsonValueKind.Number &&
                el[1].ValueKind == JsonValueKind.Number)
            {
                var lon = el[0].GetDouble();
                var lat = el[1].GetDouble();
                minLon = Math.Min(minLon, lon);
                maxLon = Math.Max(maxLon, lon);
                minLat = Math.Min(minLat, lat);
                maxLat = Math.Max(maxLat, lat);
                found = true;
            }
            else
            {
                foreach (var child in el.EnumerateArray())
                    stack.Push(child);
            }
        }

        return found ? (minLon, minLat, maxLon, maxLat) : null;
    }

/* Traverse -------------------------------------------------------- */
    private void Traverse(JsonElement feature)
    {
        /* 1️⃣  feature-level bbox (if present) */
        (double minLon, double minLat, double maxLon, double maxLat)? featBox = null;
        if (feature.TryGetProperty("bbox", out var fb) && fb.GetArrayLength() == 4)
            featBox = (fb[0].GetDouble(), fb[1].GetDouble(),
                fb[2].GetDouble(), fb[3].GetDouble());

        /* 2️⃣  grab geometry *before* diving into properties */
        JsonElement? geometry = null;
        if (feature.TryGetProperty("geometry", out var g))
            geometry = g;

        /* 3️⃣  move to properties block (where id/urls live) */
        var node = feature;
        if (feature.TryGetProperty("properties", out var props))
            node = props;

        /* 4️⃣  identify country by id + url */
        if (node.TryGetProperty("id", out var idProp) &&
            node.TryGetProperty("urls", out var urlsProp) &&
            (urlsProp.TryGetProperty("pbf", out var pbfProp) ||
             urlsProp.TryGetProperty("osm.pbf", out pbfProp)))
        {
            var id = idProp.GetString()!;
            var url = pbfProp.GetString()!;
            _countryToPbfUrl[id] = url;

            /* ISO-2 aliases */
            List<string> iso2 = new();
            if (node.TryGetProperty("iso3166-1:alpha2", out var isoArr))
                iso2.AddRange(isoArr.EnumerateArray()
                    .Select(x => x.GetString()!.ToLowerInvariant()));
            foreach (var iso in iso2)
                _countryToPbfUrl[iso] = url;

            /* 5️⃣  if bbox still null, derive from geometry */
            if (featBox is null && geometry is JsonElement geom &&
                geom.TryGetProperty("coordinates", out var coords))
                featBox = DeriveBbox(coords);

            /* 6️⃣  store envelope */
            if (featBox is not null)
            {
                _countryToEnvelope[id] = featBox.Value;
                foreach (var iso in iso2)
                    _countryToEnvelope[iso] = featBox.Value;
            }
        }

        /* 7️⃣  recurse (v1 uses “features”, legacy uses “children”) */
        if (feature.TryGetProperty("features", out var feats))
            foreach (var child in feats.EnumerateArray())
                Traverse(child);

        if (feature.TryGetProperty("children", out var kids))
            foreach (var child in kids.EnumerateArray())
                Traverse(child);
    }

    public (double minLon, double minLat, double maxLon, double maxLat) GetCountryEnvelope(string countryCode)
    {
        EnsureIndexLoaded();
        if (!_countryToEnvelope.TryGetValue(countryCode, out var env))
            throw new NotSupportedException($"Country '{countryCode}' not found in Geofabrik index.");
        return env;
    }
}