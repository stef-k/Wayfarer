namespace Wayfarer.Parsers;

public interface ITripExportService
{
    /// <summary>
    /// Builds a KML in our internal format that round-trips 1:1.
    /// </summary>
    string GenerateWayfarerKml(Guid tripId);

    /// <summary>
    /// Builds a KML compatible with Google My Maps.
    /// </summary>
    string GenerateGoogleMyMapsKml(Guid tripId);

    /// <summary>
    /// Renders an A4 PDF guide for the trip.
    /// </summary>
    /// <param name="tripId">The trip ID to export</param>
    /// <param name="progressChannel">Optional SSE channel for real-time progress updates</param>
    Task<Stream> GeneratePdfGuideAsync(Guid tripId, string? progressChannel = null);
}