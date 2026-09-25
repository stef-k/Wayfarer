using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Configuration;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Canonical thumbnail identity and the authoritative external HTTP boundary.</summary>
[Collection(PlaywrightEnvironmentTestCollection.Name)]
[PlaywrightEnvironmentIsolation]
public sealed class TripThumbnailStorageTests
{
    /// <summary>Generated names retain GUID-D, dimensions, and query-only cache versioning.</summary>
    [Fact]
    public void CanonicalIdentityResolvesOnlyCurrentRoot()
    {
        using var directory = new TestDirectory();
        var paths = TestDirectory.Storage(directory.Path);
        var storage = new TripThumbnailStorage(paths);
        var id = Guid.NewGuid();
        var updated = DateTime.UtcNow;
        var name = TripThumbnailStorage.FileName(id, 800, 450);
        Assert.Equal($"{id:D}-800x450.jpg", name);
        Assert.Equal(Path.Combine(paths.Thumbnails, "trips"), storage.Root);
        Assert.True(TripThumbnailStorage.TryParse(name, out var parsed));
        Assert.Equal(id, parsed);
        var url = storage.PublicUrl(id, 800, 450, updated);
        Assert.Equal($"/thumbs/trips/{name}?v={updated.Ticks}", url);
        Assert.True(storage.TryResolvePublicUrl(url, out var path));
        Assert.Equal(Path.Combine(storage.Root, name), path);
        Assert.Throws<ArgumentOutOfRangeException>(() => TripThumbnailStorage.FileName(id, 0, 450));
    }

    /// <summary>Paths, malformed dimensions, alternate formats and namespaces fail closed.</summary>
    [Theory]
    [InlineData("../file.jpg")]
    [InlineData("00000000-0000-0000-0000-000000000000-0x450.jpg")]
    [InlineData("00000000-0000-0000-0000-000000000000-0800x450.jpg")]
    [InlineData("00000000-0000-0000-0000-000000000000-800x450.png")]
    [InlineData("00000000-0000-0000-0000-000000000000-800x450.jpg\n")]
    [InlineData("00000000-0000-0000-0000-000000000000-999999999999x450.jpg")]
    [InlineData("nested\\00000000-0000-0000-0000-000000000000-800x450.jpg")]
    public void RejectsNonCanonicalFiles(string name)
    {
        using var directory = new TestDirectory();
        var storage = new TripThumbnailStorage(TestDirectory.Storage(directory.Path));
        Assert.False(TripThumbnailStorage.TryParse(name, out _));
        Assert.False(storage.TryResolvePublicUrl("/thumbs/trips/" + name, out _));
        Assert.Throws<ArgumentException>(() => storage.Resolve(name));
    }

    /// <summary>URLs outside the exact generated namespace cannot resolve into a physical file.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("data:image/jpeg;base64,abc")]
    [InlineData("https://example.com/thumbs/trips/")]
    [InlineData("/thumbs/other/")]
    [InlineData("/thumbs/trips/../trips/")]
    [InlineData("/thumbs/trips/%2e%2e/")]
    [InlineData("/thumbs/trips-extra/")]
    public void RejectsUnrelatedUrls(string? prefix)
    {
        using var directory = new TestDirectory();
        var storage = new TripThumbnailStorage(TestDirectory.Storage(directory.Path));
        Assert.False(storage.TryResolvePublicUrl(prefix + TripThumbnailStorage.FileName(Guid.NewGuid(), 1, 1), out _));
    }

    /// <summary>Capture publishes externally; HTTP serves those bytes and never resurrects legacy misses.</summary>
    [Fact]
    public async Task GeneratedExternalJpegIsFreshAndAuthoritativeOverLegacyWebroot()
    {
        using var directory = new TestDirectory();
        var storage = new TripThumbnailStorage(TestDirectory.Storage(directory.Path));
        var webroot = Path.Combine(directory.Path, "published", "wwwroot");
        Directory.CreateDirectory(Path.Combine(webroot, "thumbs", "trips"));
        var id = Guid.NewGuid();
        var name = TripThumbnailStorage.FileName(id, 800, 450);
        var legacy = Path.Combine(webroot, "thumbs", "trips", name);
        await File.WriteAllTextAsync(legacy, "legacy");
        var compiledDirectories = new[] { webroot, Path.Combine(webroot, "thumbs"), Path.GetDirectoryName(legacy)! };
        try
        {
            if (!OperatingSystem.IsWindows())
                foreach (var path in compiledDirectories)
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            var bytes = new byte[] { 0xff, 0xd8, 0xff, 0xd9 };
            var captures = 0;
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
            }).Build();
            var generator = new TripMapThumbnailGenerator(NullLogger<TripMapThumbnailGenerator>.Instance,
                storage, config, _ => { captures++; return Task.FromResult<byte[]?>(bytes); });
            var updated = DateTime.UtcNow.AddMinutes(-1);
            var url = await generator.GetOrGenerateThumbnailAsync(id, 10, 20, 5, 800, 450, updated);
            Assert.Equal(url, await generator.GetOrGenerateThumbnailAsync(id, 10, 20, 5, 800, 450, updated));
            Assert.Equal(1, captures);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(storage.Resolve(name)));
            using var provider = new PhysicalFileProvider(webroot);
            using var host = await new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services => services.AddRouting()).Configure(app =>
            {
                storage.MapStaticFiles(app);
                app.UseRouting();
                app.UseStaticFiles(new StaticFileOptions { FileProvider = provider });
                // Compiled legacy asset endpoints must not preempt current-root serving either.
                app.UseEndpoints(endpoints => endpoints.MapGet("/thumbs/trips/" + name,
                    context => context.Response.WriteAsync("compiled legacy")));
            })).StartAsync();
            var client = host.GetTestClient();
            var response = await client.GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("image/jpeg", response.Content.Headers.ContentType!.MediaType);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Contains("max-age=2592000", response.Headers.CacheControl!.ToString());
            Assert.Contains("immutable", response.Headers.CacheControl.ToString());
            generator.InvalidateThumbnails(id, updated);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(url)).StatusCode);
            Assert.Equal("legacy", await File.ReadAllTextAsync(legacy));
            await host.StopAsync();
        }
        finally
        {
            if (!OperatingSystem.IsWindows())
                foreach (var path in compiledDirectories)
                    File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }
}
