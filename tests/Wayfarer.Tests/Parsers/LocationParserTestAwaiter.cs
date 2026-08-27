using System.Runtime.CompilerServices;
using Wayfarer.Models;

namespace Wayfarer.Parsers;

/// <summary>Lets legacy parser assertions materialize an explicitly requested test result.</summary>
internal static class LocationParserTestAwaiter
{
    public static TaskAwaiter<List<Location>> GetAwaiter(this IAsyncEnumerable<Location> source) =>
        MaterializeAsync(source).GetAwaiter();

    private static async Task<List<Location>> MaterializeAsync(IAsyncEnumerable<Location> source)
    {
        var result = new List<Location>();
        await foreach (var location in source) result.Add(location);
        return result;
    }
}
