using Xunit;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>
/// Serializes tests that share <see cref="Wayfarer.Services.ImageProxyService"/> static coordination state.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ImageProxyStaticStateTestCollection
{
    /// <summary>Stable collection name for tests using the real image proxy service.</summary>
    public const string Name = "Image proxy static state";
}
