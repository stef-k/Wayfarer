using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Moq;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// File-system behaviors for the map thumbnail generator (Playwright-free paths).
/// </summary>
public class TripMapThumbnailGeneratorTests : IDisposable
{
    private readonly string _root;
    private readonly Mock<ILogger<TripMapThumbnailGenerator>> _logger = new();
    private readonly Mock<IWebHostEnvironment> _env = new();
    private readonly IConfiguration _config;

    public TripMapThumbnailGeneratorTests()
    {
        _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _env.SetupGet(e => e.WebRootPath).Returns(_root);
        _config = new ConfigurationBuilder().Build();
    }

    [Fact]
    public async Task GetOrGenerateThumbnailAsync_ReturnsNull_WhenCoordinatesInvalid()
    {
        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, _config);

        var result = await generator.GetOrGenerateThumbnailAsync(
            Guid.NewGuid(), 200, 10, 5, 200, 200, DateTime.UtcNow);

        Assert.Null(result);
    }

    [Fact]
    public void DeleteThumbnails_RemovesFilesForTrip()
    {
        var tripId = Guid.NewGuid();
        var path = Path.Combine(_root, "thumbs", "trips");
        Directory.CreateDirectory(path);
        var mine = Path.Combine(path, $"{tripId}-800x450.jpg");
        var other = Path.Combine(path, $"{Guid.NewGuid()}-800x450.jpg");
        File.WriteAllBytes(mine, new byte[] { 1, 2, 3 });
        File.WriteAllBytes(other, new byte[] { 4, 5, 6 });

        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, _config);

        generator.DeleteThumbnails(tripId);

        Assert.False(File.Exists(mine));
        Assert.True(File.Exists(other));
    }

    [Fact]
    public async Task CleanupOrphanedThumbnails_RemovesNonExistingTrips()
    {
        var keep = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var path = Path.Combine(_root, "thumbs", "trips");
        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, _config);
        Directory.CreateDirectory(path);
        File.WriteAllBytes(Path.Combine(path, $"{keep:N}-800x450.jpg"), new byte[] { 1 });
        File.WriteAllBytes(Path.Combine(path, $"{orphan:N}-800x450.jpg"), new byte[] { 2 });

        var deleted = await generator.CleanupOrphanedThumbnailsAsync(new HashSet<Guid> { keep });

        Assert.Equal(1, deleted);
        Assert.True(File.Exists(Path.Combine(path, $"{keep:N}-800x450.jpg")));
        Assert.False(File.Exists(Path.Combine(path, $"{orphan:N}-800x450.jpg")));
    }

    [Fact]
    public void InvalidateThumbnails_RemovesTripFiles()
    {
        var tripId = Guid.NewGuid();
        var path = Path.Combine(_root, "thumbs", "trips");
        Directory.CreateDirectory(path);
        var file = Path.Combine(path, $"{tripId}-800x450.jpg");
        File.WriteAllBytes(file, new byte[] { 1, 2 });

        var generator = new TripMapThumbnailGenerator(_logger.Object, _env.Object, _config);

        generator.InvalidateThumbnails(tripId, DateTime.UtcNow);

        Assert.False(File.Exists(file));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* ignore */ }
    }
}
