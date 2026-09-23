using Microsoft.Extensions.Options;
using Wayfarer.Models.Options;

namespace Wayfarer.Services;

/// <summary>
/// Immutable, side-effect-free runtime path authority. Subsystem adoption is staged separately.
/// Resolution is lexical: callers must control symlinks and directory ownership before file access.
/// </summary>
public sealed class StoragePaths
{
    /// <summary>Resolves configuration using the current platform and user environment without filesystem writes.</summary>
    public StoragePaths(IOptions<StorageOptions> options, IHostEnvironment environment)
        : this(options.Value, environment, OperatingSystem.IsWindows(),
            Environment.GetFolderPath, Environment.GetEnvironmentVariable, Path.GetTempPath())
    {
    }

    /// <summary>Allows deterministic platform-default tests without changing process-wide environment variables.</summary>
    internal StoragePaths(StorageOptions options, IHostEnvironment environment, bool windows,
        Func<Environment.SpecialFolder, string> folder, Func<string, string?> variable, string systemTemp)
    {
        DataRoot = ResolveRoot(options.DataRoot, nameof(StorageOptions.DataRoot), () => windows
            ? Path.Combine(UserFolder(Environment.SpecialFolder.LocalApplicationData), "Wayfarer", "Data")
            : Path.Combine(XdgHome("XDG_DATA_HOME", ".local/share"), "Wayfarer"));
        CacheRoot = ResolveRoot(options.CacheRoot, nameof(StorageOptions.CacheRoot), () => windows
            ? Path.Combine(UserFolder(Environment.SpecialFolder.LocalApplicationData), "Wayfarer", "Cache")
            : Path.Combine(XdgHome("XDG_CACHE_HOME", ".cache"), "Wayfarer"));
        LogRoot = ResolveRoot(options.LogRoot, nameof(StorageOptions.LogRoot), () => windows
            ? Path.Combine(UserFolder(Environment.SpecialFolder.LocalApplicationData), "Wayfarer", "Logs")
            : Path.Combine(XdgHome("XDG_STATE_HOME", ".local/state"), "Wayfarer", "logs"));
        TempRoot = ResolveRoot(options.TempRoot, nameof(StorageOptions.TempRoot), () =>
            Path.Combine(!windows && Path.IsPathFullyQualified(variable("TMPDIR") ?? "")
                ? variable("TMPDIR")! : systemTemp, "wayfarer"));

        // Special-folder failure must not turn a per-user default into a repository-relative path.
        string UserFolder(Environment.SpecialFolder specialFolder)
        {
            var path = folder(specialFolder);
            if (!Path.IsPathFullyQualified(path))
                throw new InvalidOperationException($"Cannot resolve the per-user {specialFolder} directory.");
            return path;
        }

        // XDG relative/empty values are invalid and use the per-user fallback.
        string XdgHome(string name, string fallback)
        {
            var value = variable(name);
            return Path.IsPathFullyQualified(value ?? "")
                ? value! : Path.Combine(UserFolder(Environment.SpecialFolder.UserProfile), fallback);
        }

        // Explicit empty values are configuration errors, not requests for defaults.
        string ResolveRoot(string? configured, string name, Func<string> developmentDefault)
        {
            if (configured is null && !environment.IsDevelopment())
                throw new InvalidOperationException($"Storage:{name} must be configured outside Development.");
            var value = configured ?? developmentDefault();
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException($"Storage:{name} must not be empty.");
            return Path.GetFullPath(value, environment.ContentRootPath);
        }
    }

    /// <summary>Absolute durable data root.</summary>
    public string DataRoot { get; }
    /// <summary>Absolute rebuildable cache root.</summary>
    public string CacheRoot { get; }
    /// <summary>Absolute operational log root.</summary>
    public string LogRoot { get; }
    /// <summary>Absolute ephemeral working root.</summary>
    public string TempRoot { get; }
    /// <summary>Target upload directory; existing upload consumers have not adopted it yet.</summary>
    public string Uploads => Path.Combine(DataRoot, "uploads");
    /// <summary>Target key-ring directory; does not change Data Protection identity or configuration.</summary>
    public string DataProtection => Path.Combine(DataRoot, "data-protection");
    /// <summary>Target tile cache directory.</summary>
    public string Tiles => Path.Combine(CacheRoot, "tiles");
    /// <summary>Target image cache directory.</summary>
    public string Images => Path.Combine(CacheRoot, "images");
    /// <summary>Target generated thumbnail directory.</summary>
    public string Thumbnails => Path.Combine(CacheRoot, "thumbnails");

    /// <summary>
    /// Resolves a logical file reference beneath an absolute selected root using native path semantics.
    /// Rejects empty, rooted, directory-only and escaping references. Does not inspect symlinks or file existence.
    /// </summary>
    public static string ResolveFile(string root, string reference)
    {
        if (!Path.IsPathFullyQualified(root))
            throw new ArgumentException("The selected root must be absolute.", nameof(root));
        if (string.IsNullOrWhiteSpace(reference) || Path.IsPathRooted(reference) ||
            reference.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || Path.EndsInDirectorySeparator(reference))
            throw new ArgumentException("A logical relative file reference is required.", nameof(reference));
        var name = Path.GetFileName(reference);
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("A file name is required.", nameof(reference));

        var resolved = Path.GetFullPath(reference, root);
        var relative = Path.GetRelativePath(root, resolved);
        if (relative == "." || relative == ".." || Path.IsPathRooted(relative) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException("The file reference escapes the selected root.", nameof(reference));
        return resolved;
    }
}
