using ImageMagick;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Protects accepted still-image behavior and single-frame authority.</summary>
public sealed class ImageProxyHelperOptimizationTests
{
    /// <summary>Static formats supported by the existing proxy pass decoded preflight.</summary>
    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    [InlineData("gif")]
    public void Preflight_AcceptsSupportedStaticFormats(string format)
    {
        var result = ImageProxyHelper.PreflightDecodedResources(CreateStaticImage(format));

        Assert.Equal(DecodedImageResourceDecision.Accepted, result.Decision);
    }

    /// <summary>Every supported single-frame input completes the established PNG/JPEG output route.</summary>
    [Theory]
    [InlineData("jpeg", false, MagickFormat.Jpeg)]
    [InlineData("png", true, MagickFormat.Png)]
    [InlineData("webp", true, MagickFormat.Png)]
    [InlineData("gif", true, MagickFormat.Png)]
    public void OptimizeImage_AcceptsSupportedStillFormats(
        string format,
        bool expectedPng,
        MagickFormat expectedOutputFormat)
    {
        var output = ImageProxyHelper.OptimizeImage(
            CreateStaticImage(format),
            null,
            null,
            90,
            out var isPng);

        using var frames = new MagickImageCollection(output);
        var decoded = Assert.Single(frames);
        Assert.Equal(expectedPng, isPng);
        Assert.Equal(expectedOutputFormat, decoded.Format);
    }

    /// <summary>Static GIF validates; multiple frames reject through full contained decoding.</summary>
    [Fact]
    public void ValidateRaster_RejectsMultipleGifFrames()
    {
        Assert.Equal("image/gif", ImageProxyHelper.ValidateRaster(CreateAnimation(1, "gif")));
        Assert.ThrowsAny<Exception>(() => ImageProxyHelper.ValidateRaster(CreateAnimation(2, "gif")));
    }

    /// <summary>Original-byte validation rejects the same animated inputs as optimized output.</summary>
    [Theory]
    [InlineData("gif")]
    [InlineData("webp")]
    public void SafeRaster_RejectsAnimationsInBothModes(string format)
    {
        var bytes = CreateAnimation(2, format);
        Assert.Throws<DecodedImageResourceRejectedException>(() => ImageProxyHelper.ValidateRaster(bytes));
        Assert.Throws<DecodedImageResourceRejectedException>(() =>
            ImageProxyHelper.OptimizeImage(bytes, null, null, 95, out _));
    }

    /// <summary>An ancillary chunk between WebP frames must not hide the second frame from preflight.</summary>
    [Fact]
    public void Preflight_RejectsWebpFramesSeparatedByAncillaryChunk()
    {
        var bytes = InsertWebpChunkAfterFirstFrame(
            CreateAnimation(2, "webp"),
            "test"u8,
            [0x01, 0x02]);

        var result = ImageProxyHelper.PreflightDecodedResources(bytes);

        Assert.Equal(DecodedImageResourceDecision.TooLarge, result.Decision);
    }

    /// <summary>Opaque images retain proportional width-first/height-second resizing and JPEG routing.</summary>
    [Fact]
    public void OptimizeImage_PreservesProportionalResizeAndJpegRouting()
    {
        var output = ImageProxyHelper.OptimizeImage(
            ImageProxyTestFactory.Fixture("resize.jpeg"), 300, 100, 82, out var isPng);

        using var frames = new MagickImageCollection(output);
        var decoded = Assert.Single(frames);
        Assert.False(isPng);
        Assert.Equal(200u, decoded.Width);
        Assert.Equal(100u, decoded.Height);
        Assert.Equal(MagickFormat.Jpeg, decoded.Format);
    }

    /// <summary>Images smaller than requested bounds are not upscaled.</summary>
    [Fact]
    public void OptimizeImage_DoesNotUpscale()
    {
        var output = ImageProxyHelper.OptimizeImage(CreateStaticImage("jpeg"), 20, 20, 90, out _);

        using var frames = new MagickImageCollection(output);
        var decoded = Assert.Single(frames);
        Assert.Equal(2u, decoded.Width);
        Assert.Equal(1u, decoded.Height);
    }

    /// <summary>Transparent PNG input retains PNG routing and non-opaque alpha.</summary>
    [Fact]
    public void OptimizeImage_PreservesPngRouting()
    {
        var output = ImageProxyHelper.OptimizeImage(CreateStaticImage("png"), null, null, 90, out var isPng);

        using var frames = new MagickImageCollection(output);
        var decoded = Assert.Single(frames);
        using var pixels = decoded.GetPixels();
        Assert.True(isPng);
        Assert.Equal(MagickFormat.Png, decoded.Format);
        Assert.Equal((byte)128, pixels.GetPixel(0, 0).ToColor()!.A);
    }

    /// <summary>JPEG orientation is applied before private EXIF is stripped.</summary>
    [Fact]
    public void OptimizeImage_PreservesOrientationAndMetadata()
    {
        var input = ImageProxyTestFactory.Fixture("orientation.jpeg");
        using var source = new MagickImage(input);
        Assert.Equal(Orientation.RightTop, source.Orientation);
        Assert.Equal("Wayfarer test", source.GetExifProfile()!.GetValue(ExifTag.Software)!.Value);
        var output = ImageProxyHelper.OptimizeImage(input, null, null, 90, out _);

        using var frames = new MagickImageCollection(output);
        var decoded = Assert.Single(frames);
        Assert.Equal(1u, decoded.Width);
        Assert.Equal(2u, decoded.Height);
        Assert.Null(decoded.GetExifProfile());
        Assert.Null(decoded.GetXmpProfile());
    }

    /// <summary>The requested JPEG quality continues to affect opaque output encoding.</summary>
    [Fact]
    public void OptimizeImage_PreservesRequestedJpegQuality()
    {
        var input = ImageProxyTestFactory.Fixture("quality.jpeg");
        var lowQuality = ImageProxyHelper.OptimizeImage(input, null, null, 20, out _);
        var highQuality = ImageProxyHelper.OptimizeImage(input, null, null, 95, out _);

        Assert.False(lowQuality.SequenceEqual(highQuality));
        Assert.True(highQuality.Length > lowQuality.Length);
    }

    /// <summary>Reuses the original 2x1 semi-transparent source encodings without regenerating bytes.</summary>
    private static byte[] CreateStaticImage(string format) => ImageProxyTestFactory.Fixture($"static.{format}");

    /// <summary>Preserves one/two GIF frames and the original animated WebP rejection vector.</summary>
    private static byte[] CreateAnimation(int frameCount, string format)
    {
        return frameCount == 1
            ? ImageProxyTestFactory.Fixture($"pixel.{format}")
            : ImageProxyTestFactory.Fixture($"animated.{format}");
    }

    /// <summary>Inserts a bounded RIFF chunk after the first encoded WebP animation frame.</summary>
    private static byte[] InsertWebpChunkAfterFirstFrame(byte[] webp, ReadOnlySpan<byte> type, byte[] payload)
    {
        var frameType = "ANMF"u8;
        var offset = 12;
        while (!webp.AsSpan(offset, 4).SequenceEqual(frameType))
        {
            var payloadLength = BitConverter.ToUInt32(webp, offset + 4);
            offset = checked(offset + 8 + (int)payloadLength + (int)(payloadLength & 1));
        }

        var frameLength = BitConverter.ToUInt32(webp, offset + 4);
        var insertionOffset = checked(offset + 8 + (int)frameLength + (int)(frameLength & 1));
        var chunk = new byte[checked(8 + payload.Length + (payload.Length & 1))];
        type.CopyTo(chunk.AsSpan(0, 4));
        BitConverter.TryWriteBytes(chunk.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(chunk, 8);

        var result = new byte[checked(webp.Length + chunk.Length)];
        webp.AsSpan(0, insertionOffset).CopyTo(result);
        chunk.CopyTo(result, insertionOffset);
        webp.AsSpan(insertionOffset).CopyTo(result.AsSpan(insertionOffset + chunk.Length));
        BitConverter.TryWriteBytes(result.AsSpan(4, 4), checked((uint)result.Length - 8));
        return result;
    }
}
