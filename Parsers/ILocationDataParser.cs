using Wayfarer.Models;

namespace Wayfarer.Parsers;

public interface ILocationDataParser
{
    /// <summary>Streams valid locations in source order without owning the input stream.</summary>
    IAsyncEnumerable<Location> ParseAsync(
        Stream fileStream,
        string userId,
        CancellationToken cancellationToken = default);
}
