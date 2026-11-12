namespace Wayfarer.Parsers;

public interface ITripExportService
{
    /// <summary>Exports an authenticated owner's coherent native snapshot; inclusion is explicit and defaults off.</summary>
    Task<PersonalNativeExport> GeneratePersonalNativeAsync(Guid tripId, string owner,
        bool includePrivatePlanning, CancellationToken cancellationToken = default);

    /// <summary>
    /// Builds redacted native v5 reusable content; personal planning requires the explicit owner action.
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
    /// <param name="cancellationToken">Cancellation token to abort the PDF generation</param>
    Task<Stream> GeneratePdfGuideAsync(Guid tripId, string? progressChannel = null, CancellationToken cancellationToken = default);
}
