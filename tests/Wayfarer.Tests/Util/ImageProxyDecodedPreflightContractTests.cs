using Wayfarer.Util;
using Xunit;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Wayfarer.Tests.Util;

/// <summary>
/// Exercises decoded-resource identification at the production helper seam.
/// </summary>
public sealed class ImageProxyDecodedPreflightContractTests
{
    /// <summary>A compact encoded image declaring excessive dimensions is rejected before decode.</summary>
    [Fact]
    public void Preflight_RejectsCompactImageDeclaringExcessiveDimensions()
    {
        var bytes = PngContractFixture.Create(width: 8193, height: 1);

        var result = ImageProxyHelper.PreflightDecodedResources(bytes);

        Assert.Equal(DecodedImageResourceDecision.TooLarge, result.Decision);
        Assert.Equal("width", result.LimitName);
    }

    /// <summary>APNG declared frames are authoritative even when ImageSharp omits the count.</summary>
    [Theory]
    [InlineData(8u, 0)]
    [InlineData(9u, 1)]
    public void Preflight_EnforcesApngDeclaredFrames(uint frames, int expectedDecision)
    {
        var bytes = PngContractFixture.Create(width: 1, height: 1, apngFrames: frames);

        var authority = ApngFrameAuthority.Inspect(bytes);
        var result = ImageProxyHelper.PreflightDecodedResources(bytes);

        Assert.Equal(ApngAuthorityDecision.Animated, authority.Decision);
        Assert.Equal(expectedDecision, (int)result.Decision);
    }

    /// <summary>Malformed, duplicate, or contradictory APNG authority remains a decoder failure.</summary>
    [Theory]
    [InlineData(PngContractFixtureMode.TruncatedAnimationControl)]
    [InlineData(PngContractFixtureMode.DuplicateAnimationControl)]
    [InlineData(PngContractFixtureMode.AnimationControlAfterImageData)]
    [InlineData(PngContractFixtureMode.ContradictoryFrameCount)]
    public void Preflight_FailsMalformedApngAuthority(PngContractFixtureMode mode)
    {
        var bytes = PngContractFixture.Create(width: 1, height: 1, apngFrames: 2, mode);

        var result = ImageProxyHelper.PreflightDecodedResources(bytes);

        Assert.Equal(DecodedImageResourceDecision.Failed, result.Decision);
    }

    /// <summary>Malformed and unsupported bytes remain failures instead of policy rejections.</summary>
    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47 })]
    public void Preflight_FailsMalformedOrUnsupportedInput(byte[] bytes)
    {
        var result = ImageProxyHelper.PreflightDecodedResources(bytes);

        Assert.Equal(DecodedImageResourceDecision.Failed, result.Decision);
    }
}

/// <summary>Names the narrow APNG metadata fixture variations required by the contract.</summary>
public enum PngContractFixtureMode
{
    Valid,
    TruncatedAnimationControl,
    DuplicateAnimationControl,
    AnimationControlAfterImageData,
    ContradictoryFrameCount
}

/// <summary>Builds compact PNG/APNG headers without allocating their declared pixel buffers.</summary>
internal static class PngContractFixture
{
    public static byte[] Create(
        uint width,
        uint height,
        uint? apngFrames = null,
        PngContractFixtureMode mode = PngContractFixtureMode.Valid)
    {
        using var image = new Image<Rgba32>(1, 1);
        if (apngFrames.HasValue)
        {
            for (var frame = 1u; frame < apngFrames.Value; frame++)
            {
                image.Frames.AddFrame(image.Frames.RootFrame);
            }
        }

        using var output = new MemoryStream();
        image.SaveAsPng(output);
        var png = output.ToArray();
        if (width != 1 || height != 1)
        {
            WriteUInt32BigEndian(png.AsSpan(16, 4), width);
            WriteUInt32BigEndian(png.AsSpan(20, 4), height);
            WriteUInt32BigEndian(png.AsSpan(29, 4), ComputeCrc(png.AsSpan(12, 17)));
        }

        if (!apngFrames.HasValue)
        {
            return png;
        }

        var animationControlOffset = FindChunkOffset(png, "acTL");
        var animationControl = png.AsSpan(animationControlOffset, 20).ToArray();
        if (mode == PngContractFixtureMode.TruncatedAnimationControl)
        {
            return png.AsSpan(0, animationControlOffset + 10).ToArray();
        }

        var endOffset = FindChunkOffset(png, "IEND");
        if (mode == PngContractFixtureMode.ContradictoryFrameCount)
        {
            WriteUInt32BigEndian(png.AsSpan(animationControlOffset + 8, 4), apngFrames.Value + 1);
            WriteUInt32BigEndian(
                png.AsSpan(animationControlOffset + 16, 4),
                ComputeCrc(png.AsSpan(animationControlOffset + 4, 12)));
            return png;
        }

        return mode switch
        {
            PngContractFixtureMode.DuplicateAnimationControl =>
                [.. png.AsSpan(0, animationControlOffset).ToArray(), .. animationControl,
                    .. png.AsSpan(animationControlOffset).ToArray()],
            PngContractFixtureMode.AnimationControlAfterImageData =>
                [.. png.AsSpan(0, animationControlOffset).ToArray(),
                    .. png.AsSpan(animationControlOffset + animationControl.Length,
                        endOffset - animationControlOffset - animationControl.Length).ToArray(),
                    .. animationControl, .. png.AsSpan(endOffset).ToArray()],
            _ => png
        };
    }

    private static int FindChunkOffset(byte[] png, string chunkName)
    {
        var expected = System.Text.Encoding.ASCII.GetBytes(chunkName);
        var offset = 8;
        while (offset + 12 <= png.Length)
        {
            if (png.AsSpan(offset + 4, 4).SequenceEqual(expected))
            {
                return offset;
            }

            offset += checked((int)ReadUInt32BigEndian(png.AsSpan(offset, 4)) + 12);
        }

        throw new InvalidOperationException($"PNG fixture lacks {chunkName}.");
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];

    private static void WriteUInt32BigEndian(Span<byte> destination, uint value)
    {
        destination[0] = (byte)(value >> 24);
        destination[1] = (byte)(value >> 16);
        destination[2] = (byte)(value >> 8);
        destination[3] = (byte)value;
    }

    private static uint ComputeCrc(ReadOnlySpan<byte> bytes)
    {
        var crc = uint.MaxValue;
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : 0xEDB88320u ^ (crc >> 1);
            }
        }

        return ~crc;
    }
}
