using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using System.Buffers.Binary;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Exercises bounded RIFF/WebP frame authority at the production preflight seam.</summary>
public sealed class WebpFrameAuthorityTests
{
    /// <summary>Static WebP has no ANMF chunks and remains on the existing still-image path.</summary>
    [Fact]
    public void Preflight_AcceptsStaticWebpWithoutAnimationFrames()
    {
        var result = ImageProxyHelper.PreflightDecodedResources(WebpContractFixture.CreateStatic());

        Assert.Equal(DecodedImageResourceDecision.Accepted, result.Decision);
    }

    /// <summary>One bounded ANMF continues through ImageSharp identification as permitted still input.</summary>
    [Fact]
    public void Preflight_AcceptsOneBoundedWebpAnimationFrame()
    {
        var result = ImageProxyHelper.PreflightDecodedResources(WebpContractFixture.CreateAnimation(1));

        Assert.Equal(DecodedImageResourceDecision.Accepted, result.Decision);
    }

    /// <summary>Two adjacent bounded ANMF chunks establish positive multi-frame authority.</summary>
    [Fact]
    public void Preflight_RejectsTwoAdjacentWebpAnimationFrames()
    {
        var result = ImageProxyHelper.PreflightDecodedResources(WebpContractFixture.CreateAnimation(2));

        AssertWebpFrameRejection(result);
    }

    /// <summary>Unknown even and odd padded chunks are skipped without hiding a later ANMF.</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Preflight_RejectsWebpFramesSeparatedByUnknownChunk(int payloadLength)
    {
        var bytes = WebpContractFixture.InsertAfterFirstFrame(
            WebpContractFixture.CreateAnimation(2),
            "test"u8,
            new byte[payloadLength]);

        var result = ImageProxyHelper.PreflightDecodedResources(bytes);

        AssertWebpFrameRejection(result);
    }

    /// <summary>Unknown chunks do not increment the authoritative WebP frame count.</summary>
    [Fact]
    public void Preflight_DoesNotCountUnknownWebpChunksAsFrames()
    {
        var bytes = WebpContractFixture.InsertAfterFirstFrame(
            WebpContractFixture.CreateAnimation(1),
            "test"u8,
            [0x00, 0x00]);

        var result = ImageProxyHelper.PreflightDecodedResources(bytes);

        Assert.Equal(DecodedImageResourceDecision.Accepted, result.Decision);
    }

    /// <summary>Malformed WebP RIFF structure fails closed before ordinary identification.</summary>
    [Theory]
    [InlineData(WebpMalformedMode.RiffSize)]
    [InlineData(WebpMalformedMode.MissingFormType)]
    [InlineData(WebpMalformedMode.TruncatedChunkHeader)]
    [InlineData(WebpMalformedMode.TruncatedPayload)]
    [InlineData(WebpMalformedMode.MissingOddPadding)]
    [InlineData(WebpMalformedMode.OffsetOverflow)]
    public void Preflight_FailsMalformedWebpContainer(WebpMalformedMode mode)
    {
        var result = ImageProxyHelper.PreflightDecodedResources(WebpContractFixture.CreateMalformed(mode));

        Assert.Equal(DecodedImageResourceDecision.Failed, result.Decision);
    }

    private static void AssertWebpFrameRejection(DecodedImageResourceResult result)
    {
        Assert.Equal(DecodedImageResourceDecision.TooLarge, result.Decision);
        Assert.Equal("frame-count", result.LimitName);
        Assert.Equal(2, result.Observed);
    }
}

/// <summary>Names bounded malformed RIFF/WebP structures used by the preflight contract.</summary>
public enum WebpMalformedMode
{
    RiffSize,
    MissingFormType,
    TruncatedChunkHeader,
    TruncatedPayload,
    MissingOddPadding,
    OffsetOverflow
}

/// <summary>Builds compact deterministic WebP containers and structural mutations.</summary>
internal static class WebpContractFixture
{
    public static byte[] CreateStatic()
    {
        using var image = new Image<Rgba32>(1, 1, new Rgba32(10, 20, 30, 128));
        using var output = new MemoryStream();
        image.SaveAsWebp(output);
        return output.ToArray();
    }

    public static byte[] CreateAnimation(int frameCount)
    {
        using var image = new Image<Rgba32>(1, 1, new Rgba32(10, 20, 30, 128));
        image.Frames.AddFrame(image.Frames.RootFrame);
        using var output = new MemoryStream();
        image.Save(output, new WebpEncoder());
        var bytes = output.ToArray();
        return frameCount == 1 ? RemoveSecondFrame(bytes) : bytes;
    }

    public static byte[] InsertAfterFirstFrame(byte[] webp, ReadOnlySpan<byte> type, byte[] payload)
    {
        var firstFrame = FindChunk(webp, "ANMF"u8, 1);
        return InsertChunk(webp, ChunkEnd(webp, firstFrame), type, payload);
    }

    public static byte[] CreateMalformed(WebpMalformedMode mode)
    {
        var bytes = CreateStatic();
        return mode switch
        {
            WebpMalformedMode.RiffSize => WithRiffSize(bytes, (uint)bytes.Length),
            WebpMalformedMode.MissingFormType => WithBytes(bytes, 8, "FAIL"u8),
            WebpMalformedMode.TruncatedChunkHeader => AppendRaw(bytes, [1, 2, 3, 4]),
            WebpMalformedMode.TruncatedPayload => AppendRaw(bytes, CreateChunkHeader("test"u8, 4, [1, 2])),
            WebpMalformedMode.MissingOddPadding => AppendRaw(bytes, CreateChunkHeader("test"u8, 1, [1])),
            WebpMalformedMode.OffsetOverflow => AppendRaw(bytes, CreateChunkHeader("test"u8, uint.MaxValue, [])),
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };
    }

    private static byte[] RemoveSecondFrame(byte[] webp)
    {
        var secondFrame = FindChunk(webp, "ANMF"u8, 2);
        var end = ChunkEnd(webp, secondFrame);
        var result = new byte[webp.Length - (end - secondFrame)];
        webp.AsSpan(0, secondFrame).CopyTo(result);
        webp.AsSpan(end).CopyTo(result.AsSpan(secondFrame));
        WriteRiffSize(result);
        return result;
    }

    private static byte[] InsertChunk(byte[] webp, int offset, ReadOnlySpan<byte> type, byte[] payload)
    {
        var chunk = CreateChunkHeader(type, (uint)payload.Length, payload, includePadding: true);
        var result = new byte[checked(webp.Length + chunk.Length)];
        webp.AsSpan(0, offset).CopyTo(result);
        chunk.CopyTo(result, offset);
        webp.AsSpan(offset).CopyTo(result.AsSpan(offset + chunk.Length));
        WriteRiffSize(result);
        return result;
    }

    private static byte[] AppendRaw(byte[] webp, byte[] suffix)
    {
        var result = new byte[checked(webp.Length + suffix.Length)];
        webp.CopyTo(result, 0);
        suffix.CopyTo(result, webp.Length);
        WriteRiffSize(result);
        return result;
    }

    private static byte[] CreateChunkHeader(
        ReadOnlySpan<byte> type,
        uint declaredPayloadLength,
        byte[] payload,
        bool includePadding = false)
    {
        var padding = includePadding && payload.Length % 2 != 0 ? 1 : 0;
        var chunk = new byte[8 + payload.Length + padding];
        type.CopyTo(chunk);
        BinaryPrimitives.WriteUInt32LittleEndian(chunk.AsSpan(4, 4), declaredPayloadLength);
        payload.CopyTo(chunk, 8);
        return chunk;
    }

    private static int FindChunk(byte[] webp, ReadOnlySpan<byte> type, int occurrence)
    {
        var offset = 12;
        while (offset < webp.Length)
        {
            if (webp.AsSpan(offset, 4).SequenceEqual(type) && --occurrence == 0)
            {
                return offset;
            }

            offset = ChunkEnd(webp, offset);
        }

        throw new InvalidOperationException("WebP fixture lacks the requested chunk.");
    }

    private static int ChunkEnd(byte[] webp, int offset)
    {
        var length = BinaryPrimitives.ReadUInt32LittleEndian(webp.AsSpan(offset + 4, 4));
        return checked(offset + 8 + (int)length + (int)(length & 1));
    }

    private static byte[] WithRiffSize(byte[] bytes, uint size)
    {
        var result = bytes.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(4, 4), size);
        return result;
    }

    private static byte[] WithBytes(byte[] bytes, int offset, ReadOnlySpan<byte> replacement)
    {
        var result = bytes.ToArray();
        replacement.CopyTo(result.AsSpan(offset));
        return result;
    }

    private static void WriteRiffSize(byte[] bytes) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4, 4), checked((uint)bytes.Length - 8));
}
