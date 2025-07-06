namespace Wayfarer.Services;

public interface ITripExportService
{
    /// <summary>
    /// Builds a KML in our internal format that round-trips 1:1.
    /// </summary>
    string GenerateWayfarerKml(Guid tripId);

    /// <summary>
    /// Builds a KML compatible with Google My Maps.
    /// </summary>
    string GenerateMyMapsKml(Guid tripId);

    /// <summary>
    /// Renders an A4 PDF guide for the trip.
    /// </summary>
    Task<Stream> GeneratePdfGuideAsync(Guid tripId);
}