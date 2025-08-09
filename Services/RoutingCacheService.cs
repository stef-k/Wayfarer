using Wayfarer.Services.Helpers;

public class RoutingCacheService
{
    private readonly ILogger<RoutingCacheService> _logger;
    private readonly string _routingCacheDirectory;
    private readonly string _osmPbfDirectory;
    private readonly RoutingBuilderService _builder;
    private readonly GeofabrikCountryIndexService _indexService;

    public RoutingCacheService(
        ILogger<RoutingCacheService> logger,
        IConfiguration configuration,
        RoutingBuilderService builder,
        GeofabrikCountryIndexService indexService)
    {
        _logger = logger;
        _builder = builder;
        _indexService = indexService;

        // ⬇ read the right keys and provide fallbacks
        _routingCacheDirectory = configuration["CacheSettings:RoutingCacheDirectory"]
                                 ?? configuration["CacheSettings:RoutingCache"]
                                 ?? "RoutingCache";

        _osmPbfDirectory = configuration["CacheSettings:OsmPbfCacheDirectory"]
                           ?? configuration["CacheSettings:OsmPbfCache"]
                           ?? "OsmPbfCache";

        if (!Path.IsPathRooted(_routingCacheDirectory))
            _routingCacheDirectory = Path.Combine(Directory.GetCurrentDirectory(), _routingCacheDirectory);

        if (!Path.IsPathRooted(_osmPbfDirectory))
            _osmPbfDirectory = Path.Combine(Directory.GetCurrentDirectory(), _osmPbfDirectory);

        Directory.CreateDirectory(_routingCacheDirectory);
        Directory.CreateDirectory(_osmPbfDirectory);
    }

    public string GetRoutingFilePath(string countryCode)
    {
        return Path.Combine(_routingCacheDirectory, countryCode + ".routing");
    }

    public string GetPbfFilePath(string countryCode)
    {
        return Path.Combine(_osmPbfDirectory, countryCode + ".osm.pbf");
    }

    public bool HasRoutingFile(string countryCode)
    {
        return File.Exists(GetRoutingFilePath(countryCode));
    }

    public void DeleteRoutingFile(string countryCode)
    {
        var path = GetRoutingFilePath(countryCode);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted routing file for country: {Country}", countryCode);
        }
    }

    public void DeletePbfFile(string countryCode)
    {
        var path = GetPbfFilePath(countryCode);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted OSM PBF file for country: {Country}", countryCode);
        }
    }

    public void GenerateRoutingFile(string countryCode)
    {
        var pbfPath = GetPbfFilePath(countryCode);
        var routingPath = GetRoutingFilePath(countryCode);

        if (!File.Exists(pbfPath))
        {
            try
            {
                var url = _indexService.GetPbfUrl(countryCode);
                _logger.LogInformation("Downloading OSM PBF for country {Country} from {Url}", countryCode, url);
                using var client = new HttpClient();
                var bytes = client.GetByteArrayAsync(url).Result;
                File.WriteAllBytes(pbfPath, bytes);
                _logger.LogInformation("Downloaded and saved OSM PBF: {Path}", pbfPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download OSM PBF for country: {Country}", countryCode);
                return;
            }
        }

        try
        {
            _builder.BuildRoutingFile(pbfPath, routingPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build routing file for country: {Country}", countryCode);
        }
    }

    public string GetCacheDirectoryPath() => _routingCacheDirectory;
}