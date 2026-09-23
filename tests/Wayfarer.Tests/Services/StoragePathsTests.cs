using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Moq;
using Wayfarer.Models.Options;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Contract tests for the staged, filesystem-independent storage authority.</summary>
public class StoragePathsTests
{
    private static readonly string Base = Path.Combine(Path.GetTempPath(), "wayfarer-path-contract");

    /// <summary>Configuration providers override roots and relative values use the content root.</summary>
    [Fact]
    public void ExplicitConfigurationNormalizesAndOverridesDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:DataRoot"] = "old" })
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Storage:DataRoot"] = "data/../durable",
                ["Storage:CacheRoot"] = Path.Combine(Base, "cache"),
                ["Storage:LogRoot"] = "logs",
                ["Storage:TempRoot"] = "temp"
            }).Build();
        var options = configuration.GetSection("Storage").Get<StorageOptions>()!;
        var paths = Create(options, development: false);
        Assert.Equal(Path.Combine(Base, "durable"), paths.DataRoot);
        Assert.Equal(Path.Combine(Base, "cache"), paths.CacheRoot);
        Assert.Equal(Path.Combine(Base, "logs"), paths.LogRoot);
        Assert.Equal(Path.Combine(Base, "temp"), paths.TempRoot);
        Assert.Equal(Path.Combine(paths.DataRoot, "uploads"), paths.Uploads);
        Assert.Equal(Path.Combine(paths.DataRoot, "data-protection"), paths.DataProtection);
        Assert.Equal(Path.Combine(paths.CacheRoot, "tiles"), paths.Tiles);
        Assert.Equal(Path.Combine(paths.CacheRoot, "images"), paths.Images);
        Assert.Equal(Path.Combine(paths.CacheRoot, "thumbnails"), paths.Thumbnails);
    }

    /// <summary>Linux honors absolute XDG and TMPDIR values without touching the real user environment.</summary>
    [Fact]
    public void LinuxUsesXdgAndTempOverrides()
    {
        var paths = Create(variables: name => Path.Combine(Base, name));
        Assert.Equal(Path.Combine(Base, "XDG_DATA_HOME", "Wayfarer"), paths.DataRoot);
        Assert.Equal(Path.Combine(Base, "XDG_CACHE_HOME", "Wayfarer"), paths.CacheRoot);
        Assert.Equal(Path.Combine(Base, "XDG_STATE_HOME", "Wayfarer", "logs"), paths.LogRoot);
        Assert.Equal(Path.Combine(Base, "TMPDIR", "wayfarer"), paths.TempRoot);
    }

    /// <summary>Missing or invalid relative XDG variables use conventional home-based defaults.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative")]
    public void LinuxDefaultsUseHomeAndSystemTemp(string? variable)
    {
        var paths = Create(variables: _ => variable);
        Assert.Equal(Path.Combine(Base, "home", ".local", "share", "Wayfarer"), paths.DataRoot);
        Assert.Equal(Path.Combine(Base, "home", ".cache", "Wayfarer"), paths.CacheRoot);
        Assert.Equal(Path.Combine(Base, "home", ".local", "state", "Wayfarer", "logs"), paths.LogRoot);
        Assert.Equal(Path.Combine(Base, "system-temp", "wayfarer"), paths.TempRoot);
    }

    /// <summary>Windows selection uses LocalApplicationData and system temp, ignoring XDG and TMPDIR.</summary>
    [Fact]
    public void WindowsDefaultsUseLocalApplicationData()
    {
        var paths = Create(windows: true, variables: _ => Path.Combine(Base, "ignored"));
        Assert.Equal(Path.Combine(Base, "local-app-data", "Wayfarer", "Data"), paths.DataRoot);
        Assert.Equal(Path.Combine(Base, "local-app-data", "Wayfarer", "Cache"), paths.CacheRoot);
        Assert.Equal(Path.Combine(Base, "local-app-data", "Wayfarer", "Logs"), paths.LogRoot);
        Assert.Equal(Path.Combine(Base, "system-temp", "wayfarer"), paths.TempRoot);
    }

    /// <summary>Non-Development has no implicit roots, and explicit blank roots never silently default.</summary>
    [Fact]
    public void MissingProductionAndBlankConfiguredRootsFail()
    {
        Assert.Throws<InvalidOperationException>(() => Create(development: false));
        Assert.Throws<ArgumentException>(() => Create(new StorageOptions { DataRoot = " " }));
    }

    /// <summary>Unavailable user folders cannot silently resolve defaults beneath the checkout.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingUserFolderFails(bool windows)
    {
        Assert.Throws<InvalidOperationException>(() => new StoragePaths(new StorageOptions(), Host(true),
            windows, _ => "", _ => null, Path.Combine(Base, "system-temp")));
    }

    /// <summary>Resolution never creates even explicitly configured target directories.</summary>
    [Fact]
    public void ConstructionDoesNotCreateDirectories()
    {
        var absent = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var options = new StorageOptions
        {
            DataRoot = Path.Combine(absent, "data"), CacheRoot = Path.Combine(absent, "cache"),
            LogRoot = Path.Combine(absent, "logs"), TempRoot = Path.Combine(absent, "temp")
        };
        var paths = new StoragePaths(Options.Create(options), Host(false));
        Assert.Equal(Path.Combine(absent, "data", "uploads", "file"),
            StoragePaths.ResolveFile(paths.Uploads, "file"));
        Assert.False(Directory.Exists(absent));
    }

    /// <summary>Contained paths normalize while siblings sharing the root prefix cannot escape containment.</summary>
    [Fact]
    public void FileReferencesNormalizeWithinRoot()
    {
        Assert.Equal(Path.Combine(Base, "file.json"), StoragePaths.ResolveFile(Base, "nested/../file.json"));
        Assert.Equal(Path.Combine(Base, "nested", "file.json"), StoragePaths.ResolveFile(Base, "nested/file.json"));
        Assert.Throws<ArgumentException>(() => StoragePaths.ResolveFile(Base, Base + "-sibling/file"));
        Assert.Throws<ArgumentException>(() => StoragePaths.ResolveFile(Base, "../wayfarer-path-contract-sibling/file"));
        Assert.Throws<ArgumentException>(() => StoragePaths.ResolveFile("relative", "file"));
    }

    /// <summary>Logical file references reject empty, rooted, escaping and directory-only values.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("../file")]
    [InlineData("nested/../../file")]
    [InlineData(".")]
    [InlineData("nested/..")]
    [InlineData("nested/.")]
    [InlineData("nested/")]
    [InlineData("/rooted")]
    [InlineData("bad\0file")]
    public void InvalidReferencesAreRejected(string reference)
    {
        Assert.Throws<ArgumentException>(() => StoragePaths.ResolveFile(Base, reference));
    }

    /// <summary>Creates deterministic default-resolution inputs using native separators on the executing platform.</summary>
    private static StoragePaths Create(StorageOptions? options = null, bool development = true,
        bool windows = false, Func<string, string?>? variables = null) =>
        new(options ?? new StorageOptions(), Host(development), windows,
            folder => Path.Combine(Base, folder == Environment.SpecialFolder.UserProfile ? "home" : "local-app-data"),
            variables ?? (_ => null), Path.Combine(Base, "system-temp"));

    /// <summary>Supplies the environment and stable content root used for relative configuration.</summary>
    private static IHostEnvironment Host(bool development)
    {
        var host = new Mock<IHostEnvironment>();
        host.SetupGet(x => x.EnvironmentName).Returns(development ? Environments.Development : Environments.Production);
        host.SetupGet(x => x.ContentRootPath).Returns(Base);
        return host.Object;
    }
}
