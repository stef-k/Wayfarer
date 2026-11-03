using ImageMagick;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Actual packaged helper capacity under unchanged address-space, CPU and wall containment.</summary>
[Collection("Image helper processes")]
public sealed class ProfilePhotoHelperTests
{
    /// <summary>Retained color controls also qualify row CMS and JPEG gray/CMYK interpretation for avatars.</summary>
    [Theory]
    [InlineData("cmyk-tagged.jpg", 201, 151, 98)]
    [InlineData("cmyk-untagged.jpg", 199, 143, 69)]
    [InlineData("avatar-gray-icc.jpg", 129, 129, 129)]
    [InlineData("gamma16-linear.png", 137, 137, 137)]
    public async Task RowColorConversionPreservesKnownSrgbControls(string fixture, int red, int green, int blue)
    {
        var input = await File.ReadAllBytesAsync(Path.Combine(ImageHelperSupervisorTests.Fixtures, fixture));
        var result = await ImageHelperSupervisorTests.Supervisor.AvatarAsync(input, null, TimeSpan.FromSeconds(30), default);
        Assert.Equal(ImageTransformStatus.Success, result.Status);
        using var output = new MagickImage(result.Bytes!);
        using var pixels = output.GetPixels();
        var pixel = pixels.GetPixel(0, 0).ToColor()!;
        Assert.InRange((int)pixel.R, red - 4, red + 4);
        Assert.InRange((int)pixel.G, green - 4, green + 4);
        Assert.InRange((int)pixel.B, blue - 4, blue + 4);
    }

    /// <summary>Both 40-MP decoder classes complete full decode/color/crop/resize/encode/verify, then reap.</summary>
    [Theory]
    [InlineData("avatar-progressive-cmyk-40mp.jpg")]
    [InlineData("avatar-rgba16-40mp.png")]
    public async Task HighResolutionPipelineCompletesUnderExistingContainment(string fixture)
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(ImageHelperSupervisorTests.Fixtures, fixture));
        var result = await ImageHelperSupervisorTests.Supervisor.AvatarAsync(bytes, null, TimeSpan.FromSeconds(30), default);
        Assert.Equal(ImageTransformStatus.Success, result.Status);
        using var frames = new MagickImageCollection(result.Bytes!);
        var output = Assert.Single(frames);
        Assert.Equal((256u, 256u), (output.Width, output.Height));
        Assert.Equal(MagickFormat.WebP, output.Format);
        Assert.Null(output.GetExifProfile()); Assert.Null(output.GetColorProfile()); Assert.Null(output.GetXmpProfile());
        if (fixture.EndsWith(".png", StringComparison.Ordinal))
        {
            Assert.True(output.HasAlpha);
            using var pixels = output.GetPixels();
            Assert.InRange((int)pixels.GetPixel(128, 128).ToColor()!.A, 185, 198);
        }
        Assert.InRange(result.Bytes!.Length, 1, 256 * 1024);
        ImageHelperSupervisorTests.AssertReaped(result.ProcessId);
    }
}
