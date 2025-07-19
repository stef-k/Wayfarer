using Wayfarer.Services.Helpers;

public class RoutingCacheService
{
    private readonly ILogger<RoutingCacheService> _logger;
    private readonly string _routingCacheDirectory;
    private readonly string _osmPbfDirectory;
    private readonly RoutingBuilderService _builder;

    public RoutingCacheService(ILogger<RoutingCacheService> logger, IConfiguration configuration,
        RoutingBuilderService builder)
    {
        _logger = logger;
        _builder = builder;

        _routingCacheDirectory = configuration.GetSection("CacheSettings:RoutingCacheDirectory").Value ?? "RoutingCache";
        _osmPbfDirectory = configuration.GetSection("CacheSettings:OsmPbfCacheDirectory").Value ?? "OsmPbfCache";

        if (!Path.IsPathRooted(_routingCacheDirectory))
        {
            _routingCacheDirectory = Path.Combine(Directory.GetCurrentDirectory(), _routingCacheDirectory);
        }

        if (!Path.IsPathRooted(_osmPbfDirectory))
        {
            _osmPbfDirectory = Path.Combine(Directory.GetCurrentDirectory(), _osmPbfDirectory);
        }

        if (!Directory.Exists(_routingCacheDirectory))
        {
            Directory.CreateDirectory(_routingCacheDirectory);
        }

        if (!Directory.Exists(_osmPbfDirectory))
        {
            Directory.CreateDirectory(_osmPbfDirectory);
        }
    }

    public string GetRoutingFilePath(string region)
    {
        return Path.Combine(_routingCacheDirectory, region + ".routing");
    }

    public string GetPbfFilePath(string region)
    {
        return Path.Combine(_osmPbfDirectory, region + ".osm.pbf");
    }

    public bool HasRoutingFile(string region)
    {
        return File.Exists(GetRoutingFilePath(region));
    }

    public void DeleteRoutingFile(string region)
    {
        var path = GetRoutingFilePath(region);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted routing file for region: {Region}", region);
        }
    }

    public void DeletePbfFile(string region)
    {
        var path = GetPbfFilePath(region);
        if (File.Exists(path))
        {
            File.Delete(path);
            _logger.LogInformation("Deleted OSM PBF file for region: {Region}", region);
        }
    }

    /// <summary>
    /// Generates a .routing file from a .osm.pbf for a given region.
    /// If the PBF file is missing, it will be downloaded first.
    /// </summary>
    public void GenerateRoutingFile(string region)
    {
        var pbfPath = GetPbfFilePath(region);
        var routingPath = GetRoutingFilePath(region);

        if (!File.Exists(pbfPath))
        {
            try
            {
                var url = RegionSourceResolver.GetPbfUrl(region);
                _logger.LogInformation("Downloading OSM PBF for region {Region} from {Url}", region, url);
                using var client = new HttpClient();
                var bytes = client.GetByteArrayAsync(url).Result;
                File.WriteAllBytes(pbfPath, bytes);
                _logger.LogInformation("Downloaded and saved OSM PBF: {Path}", pbfPath);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to download OSM PBF for region: {Region}", region);
                return;
            }
        }

        try
        {
            _builder.BuildRoutingFile(pbfPath, routingPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build routing file for region: {Region}", region);
        }
    }

    public string GetCacheDirectoryPath() => _routingCacheDirectory;
}