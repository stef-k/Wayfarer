using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Exercises bounded PNG structure and decoded-resource identification at the production seam.</summary>
public sealed class ImageProxyDecodedPreflightContractTests
{
    /// <summary>A compact encoded image declaring excessive dimensions is rejected before decode.</summary>
    [Fact]
    public void Preflight_RejectsCompactImageDeclaringExcessiveDimensions()
    {
        var result = ImageProxyHelper.PreflightDecodedResources(PngContractFixture.Create(width: 8193, height: 1));

        Assert.Equal(DecodedImageResourceDecision.TooLarge, result.Decision);
        Assert.Equal("width", result.LimitName);
    }

    /// <summary>An ordinary structurally complete PNG remains accepted.</summary>
    [Fact]
    public void Preflight_AcceptsStaticPng()
    {
        var result = ImageProxyHelper.PreflightDecodedResources(PngContractFixture.Create(1, 1));

        Assert.Equal(DecodedImageResourceDecision.Accepted, result.Decision);
    }

    /// <summary>Every scanned PNG must end exactly at a complete IEND chunk.</summary>
    [Theory]
    [InlineData(PngContractFixtureMode.MissingEnd)]
    [InlineData(PngContractFixtureMode.TrailingBytes)]
    [InlineData(PngContractFixtureMode.TruncatedChunkHeader)]
    [InlineData(PngContractFixtureMode.TruncatedChunkData)]
    [InlineData(PngContractFixtureMode.TruncatedChunkCrc)]
    public void Preflight_FailsIncompleteOrTrailingPngStructure(PngContractFixtureMode mode)
    {
        var result = ImageProxyHelper.PreflightDecodedResources(PngContractFixture.Create(1, 1, mode: mode));

        Assert.Equal(DecodedImageResourceDecision.Failed, result.Decision);
    }

    /// <summary>A CRC-valid one-frame declaration continues to ordinary still identification.</summary>
    [Fact]
    public void Preflight_OneFrameAnimationControl_FollowsStillPath()
    {
        var bytes = PngContractFixture.Create(1, 1, apngFrames: 1);

        var authority = PngFrameAuthority.Inspect(bytes);
        var result = ImageProxyHelper.PreflightDecodedResources(bytes);

        Assert.Equal(PngAuthorityDecision.StillPng, authority.Decision);
        Assert.NotEqual(DecodedImageResourceDecision.TooLarge, result.Decision);
    }

    /// <summary>A CRC-valid declaration of multiple frames is sufficient for immediate rejection.</summary>
    [Fact]
    public void Preflight_MultipleDeclaredFrames_IsTooLargeBeforeLaterMalformedBytes()
    {
        var bytes = PngContractFixture.Create(
            1,
            1,
            apngFrames: 2,
            mode: PngContractFixtureMode.ValidDeclarationWithMalformedTail);

        var result = ImageProxyHelper.PreflightDecodedResources(bytes);

        Assert.Equal(DecodedImageResourceDecision.TooLarge, result.Decision);
        Assert.Equal("frame-count", result.LimitName);
        Assert.Equal(2, result.Observed);
    }

    /// <summary>Untrusted or impossible animation declarations are malformed failures.</summary>
    [Theory]
    [InlineData(PngContractFixtureMode.InvalidAnimationControlCrc, 1u)]
    [InlineData(PngContractFixtureMode.ZeroFrames, 0u)]
    [InlineData(PngContractFixtureMode.DuplicateAnimationControl, 1u)]
    [InlineData(PngContractFixtureMode.AnimationControlAfterImageData, 1u)]
    public void Preflight_FailsInvalidAnimationControl(PngContractFixtureMode mode, uint frames)
    {
        var result = ImageProxyHelper.PreflightDecodedResources(
            PngContractFixture.Create(1, 1, apngFrames: frames, mode: mode));

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

/// <summary>Names compact PNG structure variations used by the security contract.</summary>
public enum PngContractFixtureMode
{
    Valid,
    MissingEnd,
    TrailingBytes,
    TruncatedChunkHeader,
    TruncatedChunkData,
    TruncatedChunkCrc,
    InvalidAnimationControlCrc,
    ZeroFrames,
    DuplicateAnimationControl,
    AnimationControlAfterImageData,
    ValidDeclarationWithMalformedTail
}

/// <summary>Builds compact deterministic PNG structures without allocating declared dimensions.</summary>
internal static class PngContractFixture
{
    public static byte[] Create(
        uint width,
        uint height,
        uint? apngFrames = null,
        PngContractFixtureMode mode = PngContractFixtureMode.Valid)
    {
        using var image = new Image<Rgba32>(1, 1, new Rgba32(10, 20, 30, 128));
        using var output = new MemoryStream();
        image.SaveAsPng(output);
        var png = output.ToArray();
        WriteUInt32BigEndian(png.AsSpan(16, 4), width);
        WriteUInt32BigEndian(png.AsSpan(20, 4), height);
        WriteUInt32BigEndian(png.AsSpan(29, 4), ComputeCrc(png.AsSpan(12, 17)));

        var endOffset = FindChunkOffset(png, "IEND");
        if (mode == PngContractFixtureMode.MissingEnd)
        {
            return png[..endOffset];
        }

        if (mode == PngContractFixtureMode.TrailingBytes)
        {
            return [.. png, 0x01];
        }

        if (mode == PngContractFixtureMode.TruncatedChunkHeader)
        {
            return [.. png[..endOffset], 0, 0, 0, 0, (byte)'t'];
        }

        if (mode == PngContractFixtureMode.TruncatedChunkData)
        {
            return [.. png[..endOffset], 0, 0, 0, 4, (byte)'t', (byte)'E', (byte)'S', (byte)'T', 1, 2];
        }

        if (mode == PngContractFixtureMode.TruncatedChunkCrc)
        {
            return [.. png[..endOffset], 0, 0, 0, 0, (byte)'t', (byte)'E', (byte)'S', (byte)'T', 1, 2];
        }

        if (!apngFrames.HasValue)
        {
            return png;
        }

        var frames = mode == PngContractFixtureMode.ZeroFrames ? 0u : apngFrames.Value;
        var control = CreateAnimationControl(frames);
        if (mode == PngContractFixtureMode.InvalidAnimationControlCrc)
        {
            control[^1] ^= 0xFF;
        }

        var insertionOffset = mode == PngContractFixtureMode.AnimationControlAfterImageData
            ? endOffset
            : FindChunkOffset(png, "IDAT");
        var result = new List<byte>(png.Length + control.Length * 2);
        result.AddRange(png[..insertionOffset]);
        result.AddRange(control);
        if (mode == PngContractFixtureMode.DuplicateAnimationControl)
        {
            result.AddRange(control);
        }

        if (mode == PngContractFixtureMode.ValidDeclarationWithMalformedTail)
        {
            result.AddRange([0, 0, 0]);
            return [.. result];
        }

        result.AddRange(png[insertionOffset..]);
        return [.. result];
    }

    private static byte[] CreateAnimationControl(uint frames)
    {
        var chunk = new byte[20];
        WriteUInt32BigEndian(chunk.AsSpan(0, 4), 8);
        "acTL"u8.CopyTo(chunk.AsSpan(4, 4));
        WriteUInt32BigEndian(chunk.AsSpan(8, 4), frames);
        WriteUInt32BigEndian(chunk.AsSpan(12, 4), 0);
        WriteUInt32BigEndian(chunk.AsSpan(16, 4), ComputeCrc(chunk.AsSpan(4, 12)));
        return chunk;
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
