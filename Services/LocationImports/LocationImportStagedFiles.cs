using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Wayfarer.Models.Enums;

namespace Wayfarer.Services.LocationImports;

/// <summary>
/// Owns the portable import reference codec and bounded same-host legacy compatibility.
/// Resolution is lexical; configured storage and old installation roots are administrator-owned.
/// No construction, resolution or display operation moves files or rewrites persisted references.
/// </summary>
public sealed class LocationImportStagedFiles
{
    private static readonly StringComparer NativeComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly Regex Canonical = new(@"\Aimports/[0-9a-f]{32}\.(csv|gpx|kml|json|geojson)\z",
        RegexOptions.CultureInvariant);
    private readonly StoragePaths storage;

    /// <summary>Records normalized installation roots without creating directories.</summary>
    public LocationImportStagedFiles(StoragePaths storage, IHostEnvironment environment)
    {
        this.storage = storage;
        LegacyRoots = new[] { environment.ContentRootPath, AppContext.BaseDirectory }
            .Select(root => Path.GetFullPath(Path.Combine(root, "Uploads", "Temp")))
            .Distinct(NativeComparer).ToArray();
    }

    /// <summary>Physical staging directory for all new imports.</summary>
    public string DirectoryPath => Path.Combine(storage.Uploads, "imports");

    /// <summary>Known old staging authorities, for compatibility and transition reporting only.</summary>
    public IReadOnlyList<string> LegacyRoots { get; }

    /// <summary>Creates a server-owned, forward-slash reference using the type's normalized extension.</summary>
    public static string CreateReference(LocationImportFileType type)
    {
        if (!type.IsSupportedUpload()) throw new ArgumentException("Unsupported import type.", nameof(type));
        return $"imports/{Guid.NewGuid():N}{type.GetAllowedExtensions().First()}";
    }

    /// <summary>Resolves canonical references or recognized native legacy files; all other forms fail closed.</summary>
    public bool TryResolve(string? reference, [NotNullWhen(true)] out string? path)
    {
        path = null;
        if (string.IsNullOrWhiteSpace(reference)) return false;
        try
        {
            if (Canonical.IsMatch(reference))
            {
                // Validate serialized syntax before translating to native separators.
                path = StoragePaths.ResolveFile(storage.Uploads, Path.Combine("imports", reference[8..]));
                return true;
            }
            // Foreign drive/UNC syntax must never be treated as a Unix relative name.
            if (!Path.IsPathFullyQualified(reference) || reference.IndexOf('\0') >= 0 ||
                (!OperatingSystem.IsWindows() && (reference.Contains('\\') || reference.StartsWith("//"))))
                return false;
            var normalized = Path.GetFullPath(reference);
            foreach (var root in LegacyRoots)
            {
                var relative = Path.GetRelativePath(root, normalized);
                if (!IsBeneath(root, normalized) || Path.EndsInDirectorySeparator(reference)) continue;
                path = StoragePaths.ResolveFile(root, relative);
                return true;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Never include a private persisted reference in diagnostics.
        }
        return false;
    }

    /// <summary>Projects a bounded cross-platform basename without exposing any directory components.</summary>
    public static string DisplayName(string? reference)
    {
        var name = reference?.Split('/', '\\').LastOrDefault() ?? "";
        if (name.Length is 0 or > 128 || name is "." or ".." ||
            name.Any(character => char.IsControl(character) || character == ':')) return "Unavailable file";
        return name;
    }

    /// <summary>Returns distinct, non-overlapping known roots so recursive Admin totals count each file once.</summary>
    public IReadOnlyList<string> ReportingRoots() => new[] { DirectoryPath }.Concat(LegacyRoots)
        .Distinct(NativeComparer)
        .Where(root => !new[] { DirectoryPath }.Concat(LegacyRoots)
            .Any(other => !NativeComparer.Equals(root, other) && IsBeneath(other, root)))
        .ToArray();

    /// <summary>Counts known staging files once for Admin reporting; never consults persisted row paths.</summary>
    public (long Bytes, int Count) MeasureStorage()
    {
        var files = ReportingRoots().Where(Directory.Exists)
            .SelectMany(root => new DirectoryInfo(root).EnumerateFiles("*", SearchOption.AllDirectories));
        long bytes = 0;
        var count = 0;
        foreach (var file in files)
        {
            bytes += file.Length;
            count++;
        }
        return (bytes, count);
    }

    /// <summary>Uses native relative-path semantics rather than a vulnerable textual root prefix.</summary>
    private static bool IsBeneath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return relative != "." && relative != ".." && !Path.IsPathRooted(relative) &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}
