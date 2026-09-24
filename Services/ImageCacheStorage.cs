using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>Owns portable image filenames and bounded same-host legacy path compatibility.</summary>
public sealed class ImageCacheStorage
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Uses Images for every new write; the old setting is compatibility-only.</summary>
    public ImageCacheStorage(StoragePaths paths, IConfiguration configuration)
        : this(paths.Images, configuration["CacheSettings:ImageCacheDirectory"]) { }

    /// <summary>Resolves roots without creating directories or inspecting stored files.</summary>
    internal ImageCacheStorage(string currentRoot, string? legacyRoot)
    {
        CurrentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentRoot));
        var legacy = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            string.IsNullOrWhiteSpace(legacyRoot) ? "ImageCache" : legacyRoot));
        LegacyRoot = PathComparer.Equals(CurrentRoot, legacy) ? CurrentRoot : legacy;
    }

    /// <summary>The sole new-write authority.</summary>
    public string CurrentRoot { get; }
    /// <summary>The configured native compatibility authority, normalized and de-duplicated.</summary>
    public string LegacyRoot { get; }

    /// <summary>Accepts only production lowercase SHA-256 request fingerprints.</summary>
    public static bool IsCacheKey(string? key) => key?.Length == 64 && IsLowerHex(key);

    /// <summary>Creates deterministic initial or replacement references, also for explicit migration tooling.</summary>
    public static string CreateReference(string key, Guid? generation = null)
    {
        if (!IsCacheKey(key)) throw new ArgumentException("A lowercase SHA-256 cache key is required.", nameof(key));
        return generation.HasValue ? $"{key}.{generation.Value:N}.dat" : $"{key}.dat";
    }

    /// <summary>Checks the entire filename, including key agreement and exactly one optional generation.</summary>
    public static bool IsReference(string? reference, string? key)
    {
        if (!IsCacheKey(key) || reference == null) return false;
        if (reference == key + ".dat") return true;
        return reference.Length == 101 && reference.StartsWith(key + ".", StringComparison.Ordinal) &&
            reference.EndsWith(".dat", StringComparison.Ordinal) && IsLowerHex(reference.Substring(65, 32));
    }

    /// <summary>Resolves only a validated logical filename beneath the current root.</summary>
    public string CurrentPath(string reference, string key) => IsReference(reference, key)
        ? Path.Combine(CurrentRoot, reference)
        : throw new ArgumentException("A canonical image reference matching its cache key is required.");

    /// <summary>Classifies a row before reads or deletion; never accesses the filesystem.</summary>
    public string? Resolve(ImageCacheMetadata row) => Resolve(row.FilePath, row.CacheKey);

    /// <summary>Accepts logical references or exact legacy-root paths, never normalized DB path guesses.</summary>
    public string? Resolve(string? reference, string? key)
    {
        var canonical = CanonicalReference(reference, key);
        if (canonical == null) return null;
        return reference == canonical ? Path.Combine(CurrentRoot, canonical) : Path.Combine(LegacyRoot, canonical);
    }

    /// <summary>Derives a portable target filename only from a recognized source reference.</summary>
    public string? CanonicalReference(string? reference, string? key)
    {
        if (IsReference(reference, key)) return reference;
        if (string.IsNullOrEmpty(reference) || !Path.IsPathFullyQualified(reference)) return null;
        var name = Path.GetFileName(reference);
        return IsReference(name, key) && PathComparer.Equals(reference, Path.Combine(LegacyRoot, name))
            ? name : null;
    }

    /// <summary>ASCII-only hexadecimal grammar shared by keys and generation identifiers.</summary>
    private static bool IsLowerHex(string value) => value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
}
