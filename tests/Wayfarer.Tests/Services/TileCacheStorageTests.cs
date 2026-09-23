using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves strict portable references and bounded same-host compatibility.</summary>
public sealed class TileCacheStorageTests
{
    private static readonly string Provider = new('A', 64);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "tile-reference-contract");
    private readonly TileCacheStorage storage = new(Path.Combine(Root, "current"), Path.Combine(Root, "legacy"));

    /// <summary>Legacy configuration preserves the historical process-working-directory behavior.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("old-tiles")]
    public void LegacyRoot_UsesHistoricalResolution(string? configured)
    {
        var authority = new TileCacheStorage(Root, configured);
        Assert.Equal(Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? "TileCache" : configured), authority.LegacyRoot);
    }

    /// <summary>Serialization is stable and resolution preserves row identity and coordinates.</summary>
    [Fact]
    public void CanonicalReference_RequiresExactProviderAndCoordinates()
    {
        var reference = TileCacheStorage.CreateReference(Provider, 12, 2200, 1456);
        Assert.Equal(Provider + "/12_2200_1456.png", reference);
        Assert.Equal(Path.Combine(storage.CurrentRoot, Provider, "12_2200_1456.png"),
            storage.Resolve(reference, Provider, 12, 2200, 1456));
        Assert.Null(storage.Resolve(reference, new string('B', 64), 12, 2200, 1456));
        Assert.Null(storage.Resolve(reference, Provider, 12, 2201, 1456));
        Assert.Throws<ArgumentException>(() => TileCacheStorage.CreateReference(Provider.ToLowerInvariant(), 1, 1, 1));
        Assert.Throws<ArgumentException>(() => TileCacheStorage.CreateReference("ABC", 1, 1, 1));
    }

    /// <summary>Malformed serialization and foreign paths never become file authority.</summary>
    [Theory]
    [InlineData("{p}\\9_1_2.png")]
    [InlineData("{p}//9_1_2.png")]
    [InlineData("{p}/./9_1_2.png")]
    [InlineData("{p}/../9_1_2.png")]
    [InlineData("{p}/09_1_2.png")]
    [InlineData("{p}/9_1_2.PNG")]
    [InlineData("{p}/9_1_2.png/extra")]
    [InlineData("C:\\TileCache\\{p}\\9_1_2.png")]
    [InlineData("\\\\host\\TileCache\\{p}\\9_1_2.png")]
    [InlineData("/outside/{p}/9_1_2.png")]
    [InlineData("")]
    public void InvalidReference_IsRejected(string reference) =>
        Assert.Null(storage.Resolve(reference.Replace("{p}", Provider), Provider, 9, 1, 2));

    /// <summary>Only canonical OSM can own flat legacy files; scoped legacy references stay unchanged.</summary>
    [Fact]
    public void LegacyReferences_AreBoundedAndProviderSpecific()
    {
        var scoped = Path.Combine(storage.LegacyRoot, Provider, "9_1_2.png");
        Assert.Equal(scoped, storage.Resolve(scoped, Provider, 9, 1, 2));
        Assert.Null(storage.Resolve(scoped, Provider, 9, 2, 2));
        Assert.Null(storage.Resolve(Path.Combine(storage.LegacyRoot + "-other", Provider, "9_1_2.png"), Provider, 9, 1, 2));
        var flat = Path.Combine(storage.LegacyRoot, "9_1_2.png");
        var osm = TileProviderCatalog.CreateCacheIdentity(ApplicationSettings.DefaultTileProviderKey,
            ApplicationSettings.DefaultTileProviderUrlTemplate).Fingerprint;
        Assert.Equal(flat, storage.Resolve(flat, osm, 9, 1, 2));
        Assert.Equal(flat, storage.Resolve(flat, null, 9, 1, 2));
        Assert.Null(storage.Resolve(flat, Provider, 9, 1, 2));
    }

    /// <summary>Recursive enumeration counts coincident or nested authorities once.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Enumeration_DeduplicatesRoots(bool nested)
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var authority = new TileCacheStorage(root, nested ? Path.Combine(root, "legacy") : root);
        Directory.CreateDirectory(authority.LegacyRoot);
        try
        {
            File.WriteAllBytes(Path.Combine(authority.LegacyRoot, "9_1_2.png"), [1]);
            Assert.Single(authority.EnumerateFiles());
        }
        finally { Directory.Delete(root, true); }
    }
}
