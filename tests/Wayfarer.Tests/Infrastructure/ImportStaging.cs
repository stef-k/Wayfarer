using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Wayfarer.Models.Options;
using Wayfarer.Services;
using Wayfarer.Services.LocationImports;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Supplies real bounded staging authorities to import fixtures instead of arbitrary temp-file access.</summary>
internal static class ImportStaging
{
    /// <summary>The shared authority lives until testhost exits, after all parallel cases finish.</summary>
    private static readonly TestDirectory ProcessDirectory = new();

    /// <summary>Shared test installation; unique file names retain per-case isolation.</summary>
    internal static LocationImportStagedFiles Files { get; } = Create(ProcessDirectory.Path);

    /// <summary>Normal testhost exit finalizes this exact root; killed hosts use stale maintenance.</summary>
    static ImportStaging()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try { ProcessDirectory.Dispose(); }
            catch (Exception error) { Console.Error.WriteLine("Import fixture cleanup failed: " + error.Message); }
        };
    }

    /// <summary>Creates a side-effect-free authority with separate content and durable roots.</summary>
    internal static LocationImportStagedFiles Create(string root)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(x => x.ContentRootPath).Returns(Path.Combine(root, "app"));
        var storage = new StoragePaths(Options.Create(new StorageOptions
        {
            DataRoot = Path.Combine(root, "data"), CacheRoot = Path.Combine(root, "cache"),
            LogRoot = Path.Combine(root, "logs"), TempRoot = Path.Combine(root, "temp")
        }), host.Object);
        return new(storage, host.Object);
    }

    /// <summary>Known legacy staging root for existing worker and deletion recovery fixtures.</summary>
    internal static string LegacyDirectory
    {
        get
        {
            Directory.CreateDirectory(Files.LegacyRoots[0]);
            return Files.LegacyRoots[0];
        }
    }

    /// <summary>Creates a unique same-host legacy file just as the old upload controller did.</summary>
    internal static string TempFile()
    {
        var path = Path.Combine(LegacyDirectory, Guid.NewGuid().ToString("N") + ".csv");
        using var stream = File.Create(path);
        return path;
    }
}
