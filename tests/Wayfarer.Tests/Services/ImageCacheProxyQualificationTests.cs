using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Exercises portable cache storage through the real proxy with gated fake HTTP only.</summary>
[Collection(ImageProxyStaticStateTestCollection.Name)]
public sealed class ImageCacheProxyQualificationTests : TestBase
{
    /// <summary>Fresh current/legacy hits avoid HTTP; stale legacy bytes return before promotion completes.</summary>
    [Fact]
    public async Task FreshHitsAvoidOriginAndStaleLegacyRefreshPromotesInBackground()
    {
        ImageProxyService.ResetStaticStateForTesting();
        var root = CreateTestDirectory();
        var storage = new ImageCacheStorage(Path.Combine(root, "current"), Path.Combine(root, "legacy"));
        var db = CreateDbContext();
        var cache = ImageCacheStorageQualificationTests.Service(db, storage);
        var request = new ImageProxyRequest("https://example.com/owned-image.dat", Optimize: false);
        var key = ImageProxyHelper.ComputeImageCacheKey(request.Url, request.MaxWidth, request.MaxHeight, request.Quality, request.Optimize);
        var legacyPath = Path.Combine(storage.LegacyRoot, ImageCacheStorage.CreateReference(key));
        Directory.CreateDirectory(storage.LegacyRoot);
        var legacyBytes = ImageProxyTestFactory.Raster();
        await File.WriteAllBytesAsync(legacyPath, legacyBytes);
        var row = new ImageCacheMetadata
        {
            CacheKey = key, FilePath = legacyPath, ContentType = "image/jpeg", Size = legacyBytes.Length,
            CreatedAt = DateTime.UtcNow, LastAccessed = DateTime.UtcNow
        };
        db.ImageCacheMetadata.Add(row);
        await db.SaveChangesAsync();
        using var handler = new GatedOrigin();
        using var client = new HttpClient(handler);
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(x => x.GetSettings()).Returns(new ApplicationSettings());
        using var provider = new ServiceCollection().AddSingleton<IImageProxyService>(services =>
            new ImageProxyService(client, cache, settings.Object, services.GetRequiredService<IServiceScopeFactory>(),
                NullLogger<ImageProxyService>.Instance)).BuildServiceProvider();
        var proxy = provider.GetRequiredService<IImageProxyService>();
        try
        {
            Assert.Equal(ImageProxyResultStatus.FreshHit, (await proxy.GetOrFetchAsync(request, true)).Status);
            Assert.Equal(0, handler.Requests);
            Assert.Equal(legacyPath, row.FilePath);
            row.CreatedAt = DateTime.UtcNow.AddDays(-2);
            await db.SaveChangesAsync();
            var stale = await proxy.GetOrFetchAsync(request, true).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(ImageProxyResultStatus.StaleHit, stale.Status);
            Assert.Equal(legacyBytes, stale.Bytes);
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(File.Exists(legacyPath));
            Assert.Equal(legacyPath, row.FilePath);
            handler.Release.TrySetResult();
            Assert.True(await ImageProxyService.WaitForRefreshIdleForTestingAsync(key, TimeSpan.FromSeconds(5)));
            Assert.Equal(1, handler.Requests);
            Assert.True(ImageCacheStorage.IsReference(row.FilePath, key));
            Assert.False(File.Exists(legacyPath));
            var current = await proxy.GetOrFetchAsync(request, true);
            Assert.Equal(ImageProxyResultStatus.FreshHit, current.Status);
            Assert.Equal(ImageProxyTestFactory.Raster("png"), current.Bytes);
            Assert.Equal(1, handler.Requests);
        }
        finally
        {
            handler.Release.TrySetResult();
            await ImageProxyService.WaitForRefreshIdleForTestingAsync(key, TimeSpan.FromSeconds(5));
            ImageProxyService.ResetStaticStateForTesting();
        }
    }

    /// <summary>Normal origin policy repairs unsafe metadata without reading or deleting its external bytes.</summary>
    [Fact]
    public async Task OriginFetchRepairsUnsafeRowWithoutTouchingExternalFile()
    {
        var root = CreateTestDirectory();
        var storage = new ImageCacheStorage(Path.Combine(root, "current"), Path.Combine(root, "legacy"));
        var db = CreateDbContext();
        var cache = ImageCacheStorageQualificationTests.Service(db, storage);
        var request = new ImageProxyRequest("https://example.com/unsafe-repair.dat", Optimize: false);
        var key = ImageProxyHelper.ComputeImageCacheKey(request.Url, null, null, null, false);
        var outside = Path.Combine(root, "outside.dat");
        await File.WriteAllBytesAsync(outside, [7, 8]);
        var row = new ImageCacheMetadata
        {
            CacheKey = key, FilePath = outside, ContentType = "image/jpeg", Size = 2,
            CreatedAt = DateTime.UtcNow, LastAccessed = DateTime.UtcNow
        };
        db.ImageCacheMetadata.Add(row);
        await db.SaveChangesAsync();
        using var handler = new GatedOrigin();
        handler.Release.TrySetResult();
        using var client = new HttpClient(handler);
        var settings = new Mock<IApplicationSettingsService>();
        settings.Setup(x => x.GetSettings()).Returns(new ApplicationSettings());
        var proxy = new ImageProxyService(client, cache, settings.Object, ImageProxyTestFactory.ScopeFactory(client, cache, settings.Object),
            NullLogger<ImageProxyService>.Instance);
        Assert.Equal(ImageProxyResultStatus.OriginRequired, (await proxy.GetOrFetchAsync(request, false)).Status);
        Assert.Equal(outside, row.FilePath);
        Assert.Equal(0, handler.Requests);
        var fetched = await proxy.GetOrFetchAsync(request, true);
        Assert.Equal(ImageProxyResultStatus.Fetched, fetched.Status);
        Assert.Equal(ImageProxyTestFactory.Raster("png"), fetched.Bytes);
        Assert.True(ImageCacheStorage.IsReference(row.FilePath, key));
        Assert.Equal(new byte[] { 7, 8 }, await File.ReadAllBytesAsync(outside));
        Assert.Equal(1, handler.Requests);
    }

    /// <summary>Holds origin completion until the test has observed immediate stale delivery.</summary>
    private sealed class GatedOrigin : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;

        /// <summary>Returns deterministic validated raster bytes without any network connection.</summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ImageProxyTestFactory.Raster("png")) };
            response.Content.Headers.ContentType = new("image/png");
            return response;
        }
    }
}
