using Wayfarer.Models;

namespace Wayfarer.Parsers;

public interface ILocationDataParser
{
    Task<List<Location>> ParseAsync(Stream fileStream, string userId);
}