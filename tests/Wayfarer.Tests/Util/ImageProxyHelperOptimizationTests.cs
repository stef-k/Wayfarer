using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Memory;
using SixLabors.ImageSharp.Processing;
using System.Reflection;
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
    [InlineData("jpeg", false, "JPEG")]
    [InlineData("png", true, "PNG")]
    [InlineData("webp", false, "JPEG")]
    [InlineData("gif", true, "PNG")]
    public void OptimizeImage_AcceptsSupportedStillFormats(
        string format,
        bool expectedPng,
        string expectedOutputFormat)
    {
        var output = ImageProxyHelper.OptimizeImage(
            CreateStaticImage(format),
            null,
            null,
            90,
            out var isPng);

        using var decoded = Image.Load(output);
        Assert.Equal(expectedPng, isPng);
        Assert.Equal(expectedOutputFormat, decoded.Metadata.DecodedImageFormat?.Name);
        Assert.Single(decoded.Frames);
    }

    /// <summary>GIF identification accepts one frame and rejects on the second observed frame.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public void Preflight_EnforcesGifFrameSentinel(int frames, int expectedDecision)
    {
        var result = ImageProxyHelper.PreflightDecodedResources(CreateAnimation(frames, "gif"));

        Assert.Equal(expectedDecision, (int)result.Decision);
    }

    /// <summary>WebP identification accepts one frame and rejects on the second observed frame.</summary>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public void Preflight_EnforcesWebpFrameSentinel(int frames, int expectedDecision)
    {
        var result = ImageProxyHelper.PreflightDecodedResources(CreateAnimation(frames, "webp"));

        Assert.Equal(expectedDecision, (int)result.Decision);
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
        using var image = new Image<Rgb24>(400, 200, new Rgb24(10, 20, 30));
        using var input = new MemoryStream();
        image.SaveAsJpeg(input);

        var output = ImageProxyHelper.OptimizeImage(input.ToArray(), 300, 100, 82, out var isPng);

        using var decoded = Image.Load(output);
        Assert.False(isPng);
        Assert.Equal(200, decoded.Width);
        Assert.Equal(100, decoded.Height);
        Assert.Equal("JPEG", decoded.Metadata.DecodedImageFormat?.Name);
    }

    /// <summary>Images smaller than requested bounds are not upscaled.</summary>
    [Fact]
    public void OptimizeImage_DoesNotUpscale()
    {
        var output = ImageProxyHelper.OptimizeImage(CreateStaticImage("jpeg"), 20, 20, 90, out _);

        using var decoded = Image.Load(output);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(1, decoded.Height);
    }

    /// <summary>Transparent PNG input retains PNG routing and non-opaque alpha.</summary>
    [Fact]
    public void OptimizeImage_PreservesPngRouting()
    {
        var output = ImageProxyHelper.OptimizeImage(CreateStaticImage("png"), null, null, 90, out var isPng);

        using var decoded = Image.Load<Rgba32>(output);
        Assert.True(isPng);
        Assert.Equal("PNG", decoded.Metadata.DecodedImageFormat?.Name);
        Assert.NotEqual(byte.MaxValue, decoded[0, 0].A);
    }

    /// <summary>JPEG orientation and ordinary EXIF metadata retain the current pass-through behavior.</summary>
    [Fact]
    public void OptimizeImage_PreservesOrientationAndMetadata()
    {
        using var image = new Image<Rgb24>(2, 1, new Rgb24(10, 20, 30));
        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);
        image.Metadata.ExifProfile.SetValue(ExifTag.Software, "Wayfarer test");
        using var input = new MemoryStream();
        image.SaveAsJpeg(input);

        var output = ImageProxyHelper.OptimizeImage(input.ToArray(), null, null, 90, out _);

        using var decoded = Image.Load(output);
        var profile = Assert.IsType<ExifProfile>(decoded.Metadata.ExifProfile);
        Assert.Equal(2, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.True(profile.TryGetValue(ExifTag.Orientation, out var orientation));
        Assert.True(profile.TryGetValue(ExifTag.Software, out var software));
        Assert.Equal((ushort)6, orientation.Value);
        Assert.Equal("Wayfarer test", software.Value);
    }

    /// <summary>The requested JPEG quality continues to affect opaque output encoding.</summary>
    [Fact]
    public void OptimizeImage_PreservesRequestedJpegQuality()
    {
        using var image = new Image<Rgb24>(64, 64);
        image.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    row[x] = new Rgb24((byte)(x * 3), (byte)(y * 3), (byte)((x + y) * 2));
                }
            }
        });
        using var input = new MemoryStream();
        image.SaveAsJpeg(input);

        var lowQuality = ImageProxyHelper.OptimizeImage(input.ToArray(), null, null, 20, out _);
        var highQuality = ImageProxyHelper.OptimizeImage(input.ToArray(), null, null, 95, out _);

        Assert.False(lowQuality.SequenceEqual(highQuality));
        Assert.True(highQuality.Length > lowQuality.Length);
    }

    /// <summary>The production resize seam remains explicitly configured with Lanczos3.</summary>
    [Fact]
    public void OptimizationResampler_UsesLanczos3()
    {
        Assert.Same(KnownResamplers.Lanczos3, ImageProxyHelper.OptimizationResampler);
    }

    /// <summary>The dedicated allocator exposes the fixed allocation-group capacity as defense in depth.</summary>
    [Fact]
    public void ProxyAllocator_UsesFixedAllocationGroupLimit()
    {
        var configurationField = typeof(DecodedImageResourceLimits).GetField(
            "ProxyConfiguration",
            BindingFlags.NonPublic | BindingFlags.Static);
        var configuration = Assert.IsType<Configuration>(configurationField?.GetValue(null));

        Assert.NotSame(Configuration.Default.MemoryAllocator, configuration.MemoryAllocator);
        Assert.Throws<InvalidMemoryOperationException>(() =>
            configuration.MemoryAllocator.Allocate<byte>(128 * 1024 * 1024 + 1));
    }

    private static byte[] CreateStaticImage(string format)
    {
        using var image = new Image<Rgba32>(2, 1, new Rgba32(10, 20, 30, 128));
        using var output = new MemoryStream();
        switch (format)
        {
            case "jpeg":
                image.SaveAsJpeg(output);
                break;
            case "png":
                image.SaveAsPng(output);
                break;
            case "webp":
                image.SaveAsWebp(output);
                break;
            case "gif":
                image.SaveAsGif(output);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }

        return output.ToArray();
    }

    private static byte[] CreateAnimation(int frameCount, string format)
    {
        using var image = new Image<Rgba32>(1, 1, new Rgba32(10, 20, 30, 128));
        while (image.Frames.Count < frameCount)
        {
            image.Frames.AddFrame(image.Frames.RootFrame);
        }

        using var output = new MemoryStream();
        if (format == "gif")
        {
            image.Save(output, new GifEncoder());
        }
        else
        {
            image.Save(output, new WebpEncoder());
        }

        return output.ToArray();
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
