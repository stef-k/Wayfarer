namespace Wayfarer.Services;

/// <summary>
/// Result of a tile retrieval attempt, distinguishing successful data,
/// genuine absence, and transient throttling so the controller can
/// return the appropriate HTTP status code.
/// </summary>
/// <param name="TileData">Tile image bytes when retrieval succeeded, null otherwise.</param>
/// <param name="BudgetExhausted">
/// True when the tile could not be fetched because the outbound request budget
/// was exhausted. The controller should return 503 with Retry-After.
/// </param>
public sealed record TileRetrievalResult(byte[]? TileData, bool BudgetExhausted)
{
    /// <summary>Tile data retrieved successfully.</summary>
    public static TileRetrievalResult Success(byte[] data) => new(data, false);

    /// <summary>Tile not found (genuine absence or unrecoverable error).</summary>
    public static TileRetrievalResult NotFound() => new(null, false);

    /// <summary>Upstream budget exhausted — transient, client should retry.</summary>
    public static TileRetrievalResult Throttled() => new(null, true);
}
