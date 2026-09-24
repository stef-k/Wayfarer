using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Qualifies the complete persisted image filename grammar without filesystem access.</summary>
public sealed class ImageCacheStorageTests
{
    private static readonly string Key = new('a', 64);
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "image-authority");
    private readonly ImageCacheStorage storage = new(Path.Combine(Root, "current"), Path.Combine(Root, "legacy"));

    /// <summary>Initial and replacement filenames round-trip for current reads and explicit legacy conversion.</summary>
    [Fact]
    public void CanonicalAndLegacyReferencesResolveWithoutChangingGeneration()
    {
        foreach (var reference in new[] { ImageCacheStorage.CreateReference(Key),
                     ImageCacheStorage.CreateReference(Key, Guid.Parse("01234567-89ab-cdef-0123-456789abcdef")) })
        {
            Assert.True(ImageCacheStorage.IsReference(reference, Key));
            Assert.Equal(Path.Combine(storage.CurrentRoot, reference), storage.Resolve(reference, Key));
            var legacy = Path.Combine(storage.LegacyRoot, reference);
            Assert.Equal(legacy, storage.Resolve(legacy, Key));
            Assert.Equal(reference, storage.CanonicalReference(legacy, Key));
            Assert.Null(storage.Resolve(Path.Combine(storage.CurrentRoot, reference), Key));
        }
        Assert.Equal(Key + ".dat", ImageCacheStorage.CreateReference(Key));
        Assert.Equal(Key + ".0123456789abcdef0123456789abcdef.dat",
            ImageCacheStorage.CreateReference(Key, Guid.Parse("01234567-89ab-cdef-0123-456789abcdef")));
    }

    /// <summary>Root identity uses normalized native paths; compatibility configuration retains historical defaults.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("ImageCache")]
    public void RootsNormalizeAndDeduplicate(string? configured)
    {
        var root = Path.Combine(Directory.GetCurrentDirectory(), "ImageCache");
        var authority = new ImageCacheStorage(root + Path.DirectorySeparatorChar, configured);
        Assert.Equal(authority.CurrentRoot, authority.LegacyRoot);
        Assert.Equal(root, authority.LegacyRoot);
        var path = Path.Combine(root, ImageCacheStorage.CreateReference(Key));
        Assert.Equal(path, authority.Resolve(path, Key));
    }

    /// <summary>Keys must match the production lowercase SHA-256 representation exactly.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("test_key_1")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("gggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggggg")]
    public void InvalidKeysFailClosed(string? key)
    {
        Assert.False(ImageCacheStorage.IsCacheKey(key));
        Assert.Null(storage.Resolve(Key + ".dat", key));
        Assert.Throws<ArgumentException>(() => ImageCacheStorage.CreateReference(key!));
    }

    /// <summary>Neither basename guessing nor path normalization expands the legacy authority.</summary>
    [Fact]
    public void MalformedForeignAndMismatchedReferencesFailClosed()
    {
        var name = Key + ".dat";
        var invalid = new[] { "", "../" + name, "nested/" + name, "nested\\" + name,
            name.ToUpperInvariant(), Key + ".png", name + ".extra", new string('b', 64) + ".dat",
            Key + "." + new string('A', 32) + ".dat", Key + "." + new string('a', 33) + ".dat",
            Key + "." + Guid.NewGuid().ToString("D") + ".dat", Key + "." + new string('a', 32) + ".more.dat",
            Path.Combine(Root, "outside", name), Path.Combine(storage.LegacyRoot, "nested", name),
            storage.LegacyRoot + "/../legacy/" + name, "C:\\foreign\\" + name, "\\\\foreign\\cache\\" + name,
            "bad\0" + name };
        foreach (var reference in invalid)
        {
            Assert.Null(storage.Resolve(reference, Key));
            Assert.Null(storage.CanonicalReference(reference, Key));
        }
    }
}
