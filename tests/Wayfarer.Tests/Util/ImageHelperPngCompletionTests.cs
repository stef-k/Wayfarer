using System.Buffers.Binary;
using System.IO.Compression;
using SixLabors.ImageSharp;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Decisive CRC-valid zlib controls exercise admission through the contained executable.</summary>
[Collection("Image helper processes")]
public sealed class ImageHelperPngCompletionTests
{
    /// <summary>Exhausted buffers never stand in for a verified Adler trailer or Deflate end.</summary>
    [Theory]
    [InlineData("valid", true)] [InlineData("split-empty", true)] [InlineData("trailing", true)]
    [InlineData("missing-adler", false)] [InlineData("adler-one", false)]
    [InlineData("adler-two", false)] [InlineData("adler-three", false)]
    [InlineData("wrong-adler", false)] [InlineData("unterminated-deflate", false)]
    [InlineData("short-raster", false)] [InlineData("long-raster", false)]
    [InlineData("bad-crc", false)] [InlineData("eof", false)]
    public async Task ZlibCompletion_RequiresVerifiedEndAndExactPixels(string variant, bool accepted)
    {
        byte[] raw = [0, 120, 80, 40];
        var compressed = Compress(raw);
        compressed = variant switch
        {
            "missing-adler" => compressed[..^4],
            "adler-one" => compressed[..^3],
            "adler-two" => compressed[..^2],
            "adler-three" => compressed[..^1],
            "wrong-adler" => [.. compressed[..^1], (byte)(compressed[^1] ^ 1)],
            // A stored block emits every expected byte, but BFINAL is deliberately zero.
            "unterminated-deflate" => [0x78, 0x01, 0, 4, 0, 0xfb, 0xff, .. raw, .. compressed[^4..]],
            "short-raster" => Compress(raw[..^1]),
            "long-raster" => Compress([.. raw, 0]),
            "trailing" => [.. compressed, 100, 101, 102],
            _ => compressed
        };
        var chunks = variant == "split-empty"
            ? compressed.SelectMany(value => new[] { Array.Empty<byte>(), new[] { value } }).ToArray()
            : new[] { compressed };
        var png = CreatePng(1, 1, 8, 2, 0, chunks);
        if (variant == "bad-crc") png[^1] ^= 1;
        if (variant == "eof") png = png[..^1];
        var result = await ImageHelperSupervisorTests.Supervisor.TransformAsync(
            png, new ImageTransform(), TimeSpan.FromSeconds(30));
        Assert.Equal(accepted ? ImageTransformStatus.Success : ImageTransformStatus.Failed, result.Status);
        if (accepted)
        {
            using var output = Image.Load(result.Bytes!);
            Assert.Equal((1, 1), (output.Width, output.Height));
        }
        else Assert.Null(result.Bytes);
        ImageHelperSupervisorTests.AssertReaped(result.ProcessId);
    }

    /// <summary>Filtered-byte accounting covers packed-bit Adam7 empty passes and 16-bit samples.</summary>
    [Theory]
    [InlineData(1, 0, 1)] [InlineData(16, 6, 0)]
    public async Task OriginalSampleLayout_IsAdmitted(int depth, int color, int interlaced)
    {
        byte[] raw = depth == 1 ? [0, 128] : [0, 0xff, 0xff, 0, 0, 0, 0, 0x80, 0];
        var png = CreatePng(1, 1, (byte)depth, (byte)color, (byte)interlaced, [Compress(raw)]);
        var result = await ImageHelperSupervisorTests.Supervisor.TransformAsync(
            png, new ImageTransform(), TimeSpan.FromSeconds(30));
        Assert.Equal(ImageTransformStatus.Success, result.Status);
    }

    /// <summary>A post-IDAT eXIf is observed before metadata is discarded.</summary>
    [Fact]
    public async Task PostIdatExif_OrientsAndStripsMetadata()
    {
        byte[] exif = [73,73,42,0,8,0,0,0,1,0,18,1,3,0,1,0,0,0,6,0,0,0,0,0,0,0];
        var png = CreatePng(2, 1, 8, 2, 0, [Compress([0, 255, 0, 0, 0, 255, 0])], exif);
        var result = await ImageHelperSupervisorTests.Supervisor.TransformAsync(
            png, new ImageTransform(), TimeSpan.FromSeconds(30));
        using var output = Image.Load(result.Bytes!);
        Assert.Equal((1, 2), (output.Width, output.Height));
        Assert.Null(output.Metadata.ExifProfile);
    }

    /// <summary>Test construction uses the platform compressor, never a product codec implementation.</summary>
    private static byte[] Compress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Fastest, leaveOpen: true))
            zlib.Write(raw);
        return output.ToArray();
    }

    /// <summary>Create a tiny bounded PNG with intentionally chosen IDAT boundaries.</summary>
    private static byte[] CreatePng(int width, int height, byte depth, byte color,
        byte interlaced, byte[][] idats, byte[]? exif = null)
    {
        using var output = new MemoryStream();
        output.Write(new byte[] { 137,80,78,71,13,10,26,10 });
        var header = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(header, width);
        BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
        header[8] = depth;
        header[9] = color;
        header[12] = interlaced;
        Chunk(output, "IHDR"u8, header);
        foreach (var idat in idats) Chunk(output, "IDAT"u8, idat);
        if (exif is not null) Chunk(output, "eXIf"u8, exif);
        Chunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    /// <summary>Fixture CRCs remain correct when zlib bytes are deliberately invalid.</summary>
    private static void Chunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> payload)
    {
        Span<byte> integer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(integer, payload.Length);
        output.Write(integer);
        output.Write(type);
        output.Write(payload);
        uint crc = uint.MaxValue;
        foreach (var value in type.ToArray().Concat(payload.ToArray()))
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc >> 1) ^ ((crc & 1) == 0 ? 0u : 0xedb88320u);
        }
        BinaryPrimitives.WriteUInt32BigEndian(integer, ~crc);
        output.Write(integer);
    }
}
