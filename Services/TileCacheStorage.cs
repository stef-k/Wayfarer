using System.Globalization;
using Wayfarer.Models;
using Wayfarer.Util;

namespace Wayfarer.Services;

/// <summary>Owns tile references and the two administrator-controlled filesystem authorities.</summary>
public sealed class TileCacheStorage
{
    private static readonly string OsmIdentity = TileProviderCatalog.CreateCacheIdentity(
        ApplicationSettings.DefaultTileProviderKey, ApplicationSettings.DefaultTileProviderUrlTemplate).Fingerprint;
    /// <summary>Native filesystem comparison; serialized logical references remain case-sensitive.</summary>
    public static StringComparer PathComparer { get; } = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Resolves the current write root and the deprecated working-directory-relative legacy root.</summary>
    public TileCacheStorage(StoragePaths storage, IConfiguration configuration)
        : this(storage.Tiles, configuration["CacheSettings:TileCacheDirectory"]) { }

    /// <summary>Allows isolated qualification without changing the process working directory.</summary>
    internal TileCacheStorage(string currentRoot, string? legacyRoot)
    {
        CurrentRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentRoot));
        LegacyRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            string.IsNullOrWhiteSpace(legacyRoot) ? "TileCache" : legacyRoot));
    }

    /// <summary>Only authority for newly created tile files.</summary>
    public string CurrentRoot { get; }
    /// <summary>Known old root, retained for bounded compatibility without migration.</summary>
    public string LegacyRoot { get; }

    /// <summary>Creates the portable reference used by new DB rows and explicit migration tooling.</summary>
    public static string CreateReference(string provider, int zoom, int x, int y)
    {
        if (!IsProvider(provider) || zoom < 0 || x < 0 || y < 0)
            throw new ArgumentException("A canonical provider identity and nonnegative coordinates are required.");
        return provider + "/" + FileName(zoom, x, y);
    }

    /// <summary>Tests the existing uppercase SHA-256 identity without accepting configuration text.</summary>
    public static bool IsProvider(string? provider) => provider?.Length == 64 &&
        provider.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    /// <summary>Creates the physical path for a new tile.</summary>
    public string CurrentPath(string provider, int zoom, int x, int y) =>
        StoragePaths.ResolveFile(CurrentRoot, CreateReference(provider, zoom, x, y));

    /// <summary>Classifies metadata without filesystem access; invalid rows never authorize file operations.</summary>
    public string? Resolve(TileCacheMetadata row) =>
        Resolve(row.TileFilePath, row.ProviderIdentity, row.Zoom, row.X, row.Y);

    /// <summary>Accepts only exact logical references or exact known-root legacy paths matching the row.</summary>
    public string? Resolve(string? reference, string? provider, int zoom, int x, int y)
    {
        if (string.IsNullOrEmpty(reference) || zoom < 0 || x < 0 || y < 0) return null;
        var name = FileName(zoom, x, y);
        if (IsProvider(provider))
        {
            var logical = CreateReference(provider!, zoom, x, y);
            if (reference == logical) return CurrentPath(provider!, zoom, x, y);
            var legacy = Path.Combine(LegacyRoot, provider!, name);
            if (PathComparer.Equals(reference, legacy)) return legacy;
        }
        // Null provenance is usable only by retirement or proven canonical-OSM adoption.
        var flat = Path.Combine(LegacyRoot, name);
        return (provider == null || provider == OsmIdentity) && PathComparer.Equals(reference, flat)
            ? flat : null;
    }

    /// <summary>Finds a tile in priority order without scanning or guessing from a DB basename.</summary>
    public string Find(string provider, int zoom, int x, int y, bool allowFlat)
    {
        var current = CurrentPath(provider, zoom, x, y);
        if (File.Exists(current)) return current;
        var legacy = Path.Combine(LegacyRoot, provider, FileName(zoom, x, y));
        if (File.Exists(legacy)) return legacy;
        var flat = Path.Combine(LegacyRoot, FileName(zoom, x, y));
        return allowFlat && provider == OsmIdentity && File.Exists(flat) ? flat : current;
    }

    /// <summary>Recognizes the flat legacy lane without leaking path construction into service code.</summary>
    public bool IsFlatLegacyPath(string path, int zoom, int x, int y) =>
        PathComparer.Equals(path, Path.Combine(LegacyRoot, FileName(zoom, x, y)));

    /// <summary>Sidecars always remain adjacent to the already resolved tile.</summary>
    public static string Sidecar(string tilePath) => tilePath + ".meta";

    /// <summary>Enumerates known roots recursively once, including overlapping root configurations.</summary>
    public IEnumerable<string> EnumerateFiles(string pattern = "*") =>
        new[] { CurrentRoot, LegacyRoot }.Distinct(PathComparer)
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories))
            .Distinct(PathComparer);

    /// <summary>Identifies a validated tile reference or physical path for hot-cache invalidation.</summary>
    public bool TryIdentify(string? path, out string? provider, out int zoom, out int x, out int y)
    {
        provider = null;
        zoom = x = y = 0;
        if (path == null) return false;
        var parts = Path.GetFileNameWithoutExtension(path).Split('_');
        if (parts.Length != 3 || !int.TryParse(parts[0], out zoom) ||
            !int.TryParse(parts[1], out x) || !int.TryParse(parts[2], out y)) return false;
        var parent = Path.GetFileName(Path.GetDirectoryName(path));
        provider = IsProvider(parent) ? parent : null;
        if (Resolve(path, provider, zoom, x, y) != null) return true;
        return provider != null && PathComparer.Equals(path, CurrentPath(provider, zoom, x, y));
    }

    /// <summary>Returns the physical and logical representations that could own one bounded deletion candidate.</summary>
    public IEnumerable<string> ReferenceAliases(string physicalPath)
    {
        yield return physicalPath;
        if (TryIdentify(physicalPath, out var provider, out var zoom, out var x, out var y) && provider != null &&
            PathComparer.Equals(physicalPath, CurrentPath(provider, zoom, x, y)))
            yield return CreateReference(provider, zoom, x, y);
    }

    /// <summary>Serializes coordinates independently of the process culture.</summary>
    private static string FileName(int zoom, int x, int y) =>
        string.Create(CultureInfo.InvariantCulture, $"{zoom}_{x}_{y}.png");
}
