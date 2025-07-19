using Itinero;
using Itinero.IO.Osm;
using OsmSharp.Streams;
using Microsoft.Extensions.Logging;

public class RoutingBuilderService
{
    private readonly ILogger<RoutingBuilderService> _logger;

    public RoutingBuilderService(ILogger<RoutingBuilderService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Builds a .routing file from a .osm.pbf input using car, bicycle, and pedestrian profiles.
    /// </summary>
    /// <param name="pbfPath">Path to the .osm.pbf file</param>
    /// <param name="outputPath">Path where the .routing file should be saved</param>
    public void BuildRoutingFile(string pbfPath, string outputPath)
    {
        if (!File.Exists(pbfPath))
        {
            throw new FileNotFoundException("OSM PBF file not found", pbfPath);
        }

        try
        {
            _logger.LogInformation("Starting Itinero routing graph build from {PbfPath}", pbfPath);

            using var stream = File.OpenRead(pbfPath);
            var source = new PBFOsmStreamSource(stream);

            var routerDb = new RouterDb();
            routerDb.LoadOsmData(source,
                Itinero.Osm.Vehicles.Vehicle.Car,
                Itinero.Osm.Vehicles.Vehicle.Bicycle,
                Itinero.Osm.Vehicles.Vehicle.Pedestrian);

            using var output = File.OpenWrite(outputPath);
            routerDb.Serialize(output);

            _logger.LogInformation(".routing file written to {OutputPath}", outputPath);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error building routing file from {PbfPath}", pbfPath);
            throw;
        }
    }
}