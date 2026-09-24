using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Wayfarer.Models.Options;
using Wayfarer.Services;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Owns one unique filesystem fixture; never adopts a caller-selected directory.</summary>
internal sealed class TestDirectory : IDisposable
{
    /// <summary>Exact generated root, also recognized by stale-residue maintenance.</summary>
    internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
        "wayfarer-fixture-tests-" + Guid.NewGuid().ToString("N"));

    /// <summary>Resolves isolated runtime roots within an already owned fixture directory.</summary>
    internal static StoragePaths Storage(string root, string? logRoot = null)
    {
        var host = Mock.Of<IHostEnvironment>(e => e.ContentRootPath == root && e.EnvironmentName == "Development");
        return new StoragePaths(Options.Create(new StorageOptions
        {
            DataRoot = System.IO.Path.Combine(root, "data"), CacheRoot = System.IO.Path.Combine(root, "cache"),
            LogRoot = logRoot ?? System.IO.Path.Combine(root, "logs"), TempRoot = System.IO.Path.Combine(root, "temp")
        }), host);
    }

    /// <summary>Creates the fixture directory when its lifetime starts.</summary>
    internal TestDirectory() => Directory.CreateDirectory(Path);

    /// <summary>Removes only this instance's ordinary tree; leaves linked residue for diagnosis.</summary>
    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        RejectLinks(new DirectoryInfo(Path));
        Directory.Delete(Path, recursive: true);
    }

    /// <summary>Checks each level before traversal so a fixture link cannot redirect cleanup.</summary>
    private static void RejectLinks(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Refusing linked test fixture cleanup: " + item.FullName);
        if (item is DirectoryInfo directory)
            foreach (var child in directory.EnumerateFileSystemInfos()) RejectLinks(child);
    }
}
