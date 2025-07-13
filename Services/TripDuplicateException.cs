namespace Wayfarer.Services;

/// <summary>
/// Thrown when the importer is in <c>Auto</c> mode and the TripId
/// already belongs to the current user, so UI must ask “update or copy?”.
/// </summary>
public sealed class TripDuplicateException : Exception
{
    public Guid TripId { get; }

    public TripDuplicateException(Guid tripId)
        : base("Trip already exists and requires user choice.")
    {
        TripId = tripId;
    }
}