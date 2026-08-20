using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Memory;
using System.Reflection;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Protects accepted image behavior and animated decoded-resource accounting.</summary>
public sealed class ImageProxyHelperOptimizationTests
{
    /// <summary>Static formats supported by the existing proxy pass decoded preflight.</summary>
    [Theory]
    [InlineData("jpeg")]
    [InlineData("png")]
    [InlineData("webp")]
    public void Preflight_AcceptsSupportedStaticFormats(string format)
    {
        var result = ImageProxyHelper.PreflightDecodedResources(CreateStaticImage(format));

        Assert.Equal(DecodedImageResourceDecision.Accepted, result.Decision);
    }

    /// <summary>GIF identification uses the ninth observed frame only as a rejection sentinel.</summary>
    [Theory]
    [InlineData(8, 0)]
    [InlineData(9, 1)]
    public void Preflight_EnforcesGifFrameSentinel(int frames, int expectedDecision)
    {
        var result = ImageProxyHelper.PreflightDecodedResources(CreateAnimation(frames, "gif"));

        Assert.Equal(expectedDecision, (int)result.Decision);
    }

    /// <summary>Animated WebP identification uses the ninth observed frame only as a rejection sentinel.</summary>
    [Theory]
    [InlineData(8, 0)]
    [InlineData(9, 1)]
    public void Preflight_EnforcesWebpFrameSentinel(int frames, int expectedDecision)
    {
        var result = ImageProxyHelper.PreflightDecodedResources(CreateAnimation(frames, "webp"));

        Assert.Equal(expectedDecision, (int)result.Decision);
    }

    /// <summary>All accepted animation frames contribute to aggregate decoded-byte accounting.</summary>
    [Fact]
    public void Preflight_AccountsForEveryGifFrameWithoutLargeAllocation()
    {
        var bytes = CreateAnimation(8, "gif");
        bytes[6] = 0;
        bytes[7] = 8;
        bytes[8] = 1;
        bytes[9] = 4;

        var result = ImageProxyHelper.PreflightDecodedResources(bytes);

        Assert.Equal(DecodedImageResourceDecision.TooLarge, result.Decision);
        Assert.Equal("aggregate-decoded-bytes", result.LimitName);
    }

    /// <summary>Accepted animations are decoded with every original frame rather than truncated.</summary>
    [Theory]
    [InlineData("gif")]
    [InlineData("webp")]
    public void OptimizeImage_PreservesAllAcceptedAnimationFrames(string format)
    {
        var output = ImageProxyHelper.OptimizeImage(CreateAnimation(8, format), null, null, 90, out var isPng);

        using var decoded = Image.Load(output);
        Assert.Equal(format == "gif", isPng);
        Assert.Equal(format == "gif" ? 8 : 1, decoded.Frames.Count);
    }

    /// <summary>Accepted APNG input is decoded and encoded without truncating its original frames.</summary>
    [Fact]
    public void OptimizeImage_PreservesAllAcceptedApngFrames()
    {
        var output = ImageProxyHelper.OptimizeImage(
            PngContractFixture.Create(1, 1, apngFrames: 8),
            null,
            null,
            90,
            out var isPng);

        using var decoded = Image.Load(output);
        Assert.True(isPng);
        Assert.Equal(8, decoded.Frames.Count);
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

    /// <summary>PNG input retains transparency-oriented PNG output routing.</summary>
    [Fact]
    public void OptimizeImage_PreservesPngRouting()
    {
        var output = ImageProxyHelper.OptimizeImage(CreateStaticImage("png"), null, null, 90, out var isPng);

        using var decoded = Image.Load(output);
        Assert.True(isPng);
        Assert.Equal("PNG", decoded.Metadata.DecodedImageFormat?.Name);
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
}
