namespace Wayfarer.Services;

/// <summary>Indicates an import input that cannot be safely persisted.</summary>
public sealed class TripImportValidationException(string message) : Exception(message);

public enum TripImportMode
{
    Auto,       // default: upsert if owned, else copy (status-quo)
    Upsert,     // force update an existing trip you own
    CreateNew   // always clone – ignore TripId inside the file
}

public interface ITripImportService
{
    /// <summary>Parses a Wayfarer-Extended-KML file and stores it.</summary>
    /// <returns>The Trip.Id of the upserted or newly created trip.</returns>
    Task<Guid> ImportWayfarerKmlAsync(
        Stream kmlStream,
        string currentUserId,
        TripImportMode mode = TripImportMode.Auto);
}
