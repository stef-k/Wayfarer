namespace Wayfarer.LocCheck;

/// <summary>
/// Finds source files that should be checked for size limits.
/// </summary>
public sealed class SourceFileScanner
{
    private static readonly HashSet<string> IncludedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs",
        ".cshtml",
        ".js",
        ".ts",
        ".vue",
        ".css",
        ".scss"
    };

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git",
        ".idea",
        ".local",
        ".vs",
        "bin",
        "obj",
        "node_modules",
        "coverage",
        "coverage-report"
    };

    private static readonly string[] ExcludedFileSuffixes =
    [
        ".Designer.cs",
        ".g.cs",
        ".generated.cs",
        ".min.js",
        ".min.css"
    ];

    private static readonly string[] ExcludedPathPrefixes =
    [
        "ChromeCache/",
        "ImageCache/",
        "Logs/",
        "Migrations/",
        "MbtileCache/",
        "OsmPbfCache/",
        "RoutingCache/",
        "TileCache/",
        "Uploads/",
        "wwwroot/dist/",
        "wwwroot/lib/",
        "wwwroot/vite/"
    ];

    /// <summary>
    /// Enumerates source files under the supplied repository root.
    /// </summary>
    public IReadOnlyList<string> GetSourceFiles(string rootPath)
    {
        return Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories)
            .Where(path => IsIncluded(rootPath, path))
            .OrderBy(path => ToRelativePath(rootPath, path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Converts an absolute path to a normalized repository-relative path.
    /// </summary>
    public static string ToRelativePath(string rootPath, string path)
    {
        return Path.GetRelativePath(rootPath, path).Replace('\\', '/');
    }

    private static bool IsIncluded(string rootPath, string path)
    {
        var extension = Path.GetExtension(path);
        if (!IncludedExtensions.Contains(extension))
        {
            return false;
        }

        var relativePath = ToRelativePath(rootPath, path);
        if (ExcludedPathPrefixes.Any(prefix => relativePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (ExcludedFileSuffixes.Any(suffix => relativePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return !segments.Any(segment => ExcludedDirectories.Contains(segment));
    }
}
