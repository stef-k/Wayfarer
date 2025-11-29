using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;
using Moq;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests for <see cref="TripThumbnailService"/> covering thumbnail generation and fallback strategies.
/// </summary>
public class TripThumbnailServiceTests
{
    private readonly Mock<ILogger<TripThumbnailService>> _mockLogger;
    private readonly Mock<IWebHostEnvironment> _mockEnv;
    private readonly Mock<ITripMapThumbnailGenerator> _mockGenerator;

    public TripThumbnailServiceTests()
    {
        _mockLogger = new Mock<ILogger<TripThumbnailService>>();
        _mockEnv = new Mock<IWebHostEnvironment>();
        _mockGenerator = new Mock<ITripMapThumbnailGenerator>();
    }

    #region GetThumbUrl Tests

    [Fact]
    public void GetThumbUrl_ReturnsCoverImageUrl_WhenProvided()
    {
        // Arrange
        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);
        var tripId = Guid.NewGuid();
        var coverUrl = "https://example.com/cover.jpg";

        // Act
        var result = service.GetThumbUrl(tripId, null, null, null, coverUrl);

        // Assert
        Assert.Equal(coverUrl, result);
    }

    [Fact]
    public void GetThumbUrl_ReturnsPlaceholder_WhenNoCoverImage()
    {
        // Arrange
        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);
        var tripId = Guid.NewGuid();

        // Act
        var result = service.GetThumbUrl(tripId, null!, null!, null!, null!);

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("data:image/svg+xml", result);
    }

    [Fact]
    public void GetThumbUrl_ReturnsPlaceholder_WhenCoverImageIsEmpty()
    {
        // Arrange
        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);
        var tripId = Guid.NewGuid();

        // Act
        var result = service.GetThumbUrl(tripId, 40.7128, -74.0060, 10, "");

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("data:image/svg+xml", result);
    }

    [Fact]
    public void GetThumbUrl_ReturnsPlaceholder_WhenCoverImageIsWhitespace()
    {
        // Arrange
        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);
        var tripId = Guid.NewGuid();

        // Act
        var result = service.GetThumbUrl(tripId, 40.7128, -74.0060, 10, "   ");

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("data:image/svg+xml", result);
    }

    [Fact]
    public void GetThumbUrl_ReturnsCoverImage_EvenWithCoordinates()
    {
        // Arrange - currently map snapshot is not implemented, so cover image takes priority
        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);
        var tripId = Guid.NewGuid();
        var coverUrl = "https://example.com/my-trip.jpg";

        // Act
        var result = service.GetThumbUrl(tripId, 40.7128, -74.0060, 10, coverUrl);

        // Assert
        Assert.Equal(coverUrl, result);
    }

    [Fact]
    public void GetThumbUrl_AcceptsSizeParameter()
    {
        // Arrange
        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);
        var tripId = Guid.NewGuid();

        // Act - should not throw
        var result = service.GetThumbUrl(tripId, null, null, null, null, "400x300");

        // Assert
        Assert.NotNull(result);
    }

    #endregion

    #region GetThumbUrlAsync Tests

    [Fact]
    public async Task GetThumbUrlAsync_ReturnsMapSnapshot_WhenCoordinatesAvailable()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var expectedUrl = "/thumbs/trips/test-snapshot.jpg";

        _mockGenerator
            .Setup(g => g.GetOrGenerateThumbnailAsync(
                tripId, 40.7128, -74.0060, 10, 800, 450,
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedUrl);

        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);

        // Act
        var result = await service.GetThumbUrlAsync(
            tripId, 40.7128, -74.0060, 10, null, DateTime.UtcNow);

        // Assert
        Assert.Equal(expectedUrl, result);
    }

    [Fact]
    public async Task GetThumbUrlAsync_FallsToCoverImage_WhenGeneratorReturnsEmpty()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var coverUrl = "https://example.com/cover.jpg";

        _mockGenerator
            .Setup(g => g.GetOrGenerateThumbnailAsync(
                It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(string.Empty);

        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);

        // Act
        var result = await service.GetThumbUrlAsync(
            tripId, 40.7128, -74.0060, 10, coverUrl, DateTime.UtcNow);

        // Assert
        Assert.Equal(coverUrl, result);
    }

    [Fact]
    public async Task GetThumbUrlAsync_FallsToCoverImage_WhenGeneratorThrows()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var coverUrl = "https://example.com/cover.jpg";

        _mockGenerator
            .Setup(g => g.GetOrGenerateThumbnailAsync(
                It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Generation failed"));

        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);

        // Act
        var result = await service.GetThumbUrlAsync(
            tripId, 40.7128, -74.0060, 10, coverUrl, DateTime.UtcNow);

        // Assert
        Assert.Equal(coverUrl, result);
    }

    [Fact]
    public async Task GetThumbUrlAsync_ReturnsPlaceholder_WhenAllFallbacksFail()
    {
        // Arrange
        var tripId = Guid.NewGuid();

        _mockGenerator
            .Setup(g => g.GetOrGenerateThumbnailAsync(
                It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Generation failed"));

        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);

        // Act
        var result = await service.GetThumbUrlAsync(
            tripId, 40.7128, -74.0060, 10, null, DateTime.UtcNow);

        // Assert
        Assert.NotNull(result);
        Assert.StartsWith("data:image/svg+xml", result);
    }

    [Fact]
    public async Task GetThumbUrlAsync_ReturnsCoverImage_WhenNoCoordinates()
    {
        // Arrange
        var tripId = Guid.NewGuid();
        var coverUrl = "https://example.com/cover.jpg";

        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);

        // Act
        var result = await service.GetThumbUrlAsync(
            tripId, null, null, null, coverUrl, DateTime.UtcNow);

        // Assert
        Assert.Equal(coverUrl, result);
        _mockGenerator.Verify(g => g.GetOrGenerateThumbnailAsync(
            It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>(),
            It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task GetThumbUrlAsync_ParsesCustomSizeParameter()
    {
        // Arrange
        var tripId = Guid.NewGuid();

        _mockGenerator
            .Setup(g => g.GetOrGenerateThumbnailAsync(
                tripId, 40.7128, -74.0060, 10,
                400, 300, // Expect parsed dimensions
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/thumbs/test.jpg");

        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);

        // Act
        var result = await service.GetThumbUrlAsync(
            tripId, 40.7128, -74.0060, 10, null, DateTime.UtcNow, "400x300");

        // Assert
        Assert.Equal("/thumbs/test.jpg", result);
        _mockGenerator.Verify(g => g.GetOrGenerateThumbnailAsync(
            tripId, 40.7128, -74.0060, 10, 400, 300,
            It.IsAny<DateTime>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task GetThumbUrlAsync_UsesDefaultSize_WhenParsingFails()
    {
        // Arrange
        var tripId = Guid.NewGuid();

        _mockGenerator
            .Setup(g => g.GetOrGenerateThumbnailAsync(
                tripId, 40.7128, -74.0060, 10,
                800, 450, // Default dimensions
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync("/thumbs/test.jpg");

        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);

        // Act
        var result = await service.GetThumbUrlAsync(
            tripId, 40.7128, -74.0060, 10, null, DateTime.UtcNow, "invalid");

        // Assert
        Assert.Equal("/thumbs/test.jpg", result);
    }

    [Fact]
    public async Task GetThumbUrlAsync_LogsWarning_WhenGeneratorFails()
    {
        // Arrange
        var tripId = Guid.NewGuid();

        _mockGenerator
            .Setup(g => g.GetOrGenerateThumbnailAsync(
                It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>(),
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<DateTime>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("Test failure"));

        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);

        // Act
        await service.GetThumbUrlAsync(
            tripId, 40.7128, -74.0060, 10, null, DateTime.UtcNow);

        // Assert - Logger should be called
        _mockLogger.Verify(
            x => x.Log(
                LogLevel.Warning,
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, t) => true),
                It.IsAny<Exception>(),
                It.Is<Func<It.IsAnyType, Exception?, string>>((v, t) => true)),
            Times.Once);
    }

    #endregion

    #region Constructor Tests

    [Fact]
    public void Constructor_InitializesWithDependencies()
    {
        // Act
        var service = new TripThumbnailService(_mockLogger.Object, _mockEnv.Object, _mockGenerator.Object);

        // Assert
        Assert.NotNull(service);
    }

    #endregion
}
