using Moq;
using Wayfarer.Services;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>
/// Tests PDF cover snapshot conversion without invoking full Playwright PDF generation.
/// </summary>
public class TripExportCoverSnapshotBuilderTests
{
    [Fact]
    public async Task BuildDataUriAsync_UsesProxyBytesAndContentType()
    {
        // Arrange
        var coverBytes = new byte[] { 1, 2, 3 };
        var imageProxy = new Mock<IImageProxyService>();
        imageProxy
            .Setup(s => s.GetOrFetchAsync(
                It.Is<ImageProxyRequest>(r => r.Url == "https://example.com/cover.jpg"),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageProxyResult(
                ImageProxyResultStatus.Fetched,
                "cover-key",
                coverBytes,
                "image/jpeg"));

        // Act
        var dataUri = await TripExportCoverSnapshotBuilder.BuildDataUriAsync(
            imageProxy.Object,
            "https://example.com/cover.jpg");

        // Assert
        Assert.Equal("data:image/jpeg;base64,AQID", dataUri);
    }

    [Theory]
    [InlineData(ImageProxyResultStatus.BadRequest, null, null)]
    [InlineData(ImageProxyResultStatus.NotFound, null, null)]
    [InlineData(ImageProxyResultStatus.TooLarge, null, null)]
    [InlineData(ImageProxyResultStatus.Failed, null, null)]
    [InlineData(ImageProxyResultStatus.Fetched, new byte[0], "image/jpeg")]
    [InlineData(ImageProxyResultStatus.Fetched, new byte[] { 1 }, null)]
    public async Task BuildDataUriAsync_OmitsCover_WhenProxyHasNoUsableBytes(
        ImageProxyResultStatus status,
        byte[]? bytes,
        string? contentType)
    {
        // Arrange
        var imageProxy = new Mock<IImageProxyService>();
        imageProxy
            .Setup(s => s.GetOrFetchAsync(
                It.IsAny<ImageProxyRequest>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageProxyResult(status, "cover-key", bytes, contentType));

        // Act
        var dataUri = await TripExportCoverSnapshotBuilder.BuildDataUriAsync(
            imageProxy.Object,
            "https://example.com/cover.jpg");

        // Assert
        Assert.Null(dataUri);
    }

    [Fact]
    public async Task BuildDataUriAsync_OmitsCover_WhenProxyThrows()
    {
        // Arrange
        var imageProxy = new Mock<IImageProxyService>();
        imageProxy
            .Setup(s => s.GetOrFetchAsync(
                It.IsAny<ImageProxyRequest>(),
                true,
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Origin fetch failed"));

        // Act
        var dataUri = await TripExportCoverSnapshotBuilder.BuildDataUriAsync(
            imageProxy.Object,
            "https://example.com/cover.jpg");

        // Assert
        Assert.Null(dataUri);
    }

    [Fact]
    public async Task BuildDataUriAsync_PreservesNonPngContentType()
    {
        // Arrange
        var imageProxy = new Mock<IImageProxyService>();
        imageProxy
            .Setup(s => s.GetOrFetchAsync(
                It.IsAny<ImageProxyRequest>(),
                true,
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ImageProxyResult(
                ImageProxyResultStatus.Fetched,
                "cover-key",
                new byte[] { 4, 5, 6 },
                "image/webp"));

        // Act
        var dataUri = await TripExportCoverSnapshotBuilder.BuildDataUriAsync(
            imageProxy.Object,
            "https://example.com/cover.webp");

        // Assert
        Assert.Equal("data:image/webp;base64,BAUG", dataUri);
    }
}
