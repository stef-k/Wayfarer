using System.Net;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Processing.Processors.Transforms;
using Wayfarer.Services;

namespace Wayfarer.Util;

/// <summary>
/// Shared utility methods for image proxy operations: SSRF validation, cache key
/// computation, and ImageSharp optimization. Used by both TripViewerController
/// (HTTP pipeline) and ImageProxyService (background warm-up).
/// </summary>
public static class ImageProxyHelper
{
    /// <summary>The established high-quality resampler used by optimized proxy images.</summary>
    internal static IResampler OptimizationResampler => KnownResamplers.Lanczos3;

    /// <summary>
    /// Browser-like User-Agent sent by image proxy HttpClients to avoid 403 rejections
    /// from servers (e.g. Wikipedia/Wikimedia) that block requests without a User-Agent.
    /// </summary>
    public const string ProxyUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    /// <summary>
    /// Validates that a proxy URL is safe to fetch: must use http/https scheme
    /// and must not target private/loopback IP addresses (SSRF prevention).
    /// Inspects the hostname literal; DNS-level validation is performed separately
    /// via the <see cref="System.Net.Sockets.SocketsHttpHandler.ConnectCallback"/> registered in Program.cs.
    /// </summary>
    public static bool IsUrlAllowed(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return false;

        var host = uri.Host;

        // Block localhost hostnames
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            return false;

        // Block private/loopback IP address literals (including IPv6)
        if (IPAddress.TryParse(host, out var ip) && RateLimitHelper.IsPrivateOrLoopback(ip))
            return false;

        return true;
    }

    /// <summary>
    /// Computes a deterministic SHA-256 cache key from the proxy request parameters.
    /// Normalizes quality so that quality=null with optimize=true produces the same key
    /// as quality=95 with optimize=true (both resolve to the same output).
    /// </summary>
    public static string ComputeImageCacheKey(
        string url, int? maxWidth, int? maxHeight, int? quality, bool optimize)
    {
        var effectiveQuality = optimize ? (quality ?? 95) : quality;
        var raw = $"{url}|{maxWidth}|{maxHeight}|{effectiveQuality}|{optimize}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>
    /// Builds a relative proxy endpoint URL for an external image URL.
    /// Returns the original value for blank, relative, data, or already-proxied URLs.
    /// </summary>
    public static string? ToProxyUrl(string? imageUrl)
    {
        if (string.IsNullOrWhiteSpace(imageUrl) ||
            imageUrl.StartsWith("/Public/ProxyImage", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(imageUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return imageUrl;
        }

        return $"/Public/ProxyImage?url={System.Net.WebUtility.UrlEncode(imageUrl)}";
    }

    /// <summary>
    /// Optimizes an image using ImageSharp: resize and compress while maintaining quality.
    /// Preserves PNG transparency for icons, converts photos to JPEG.
    /// Uses pure managed code with no native dependencies for cross-platform support.
    /// </summary>
    public static byte[] OptimizeImage(byte[] imageBytes, int? maxWidth, int? maxHeight, int quality, out bool isPng)
    {
        var preflight = PreflightDecodedResources(imageBytes);
        if (preflight.Decision == DecodedImageResourceDecision.TooLarge)
        {
            throw new DecodedImageResourceRejectedException(preflight);
        }

        if (preflight.Decision == DecodedImageResourceDecision.Failed)
        {
            throw new InvalidImageContentException("Image metadata could not be validated.");
        }

        using var image = DecodedImageResourceLimits.Load(imageBytes.AsSpan());

        // Check if image has transparency (alpha channel)
        // PNG and WebP formats typically have alpha, JPEG does not
        bool hasTransparency = image.Metadata.DecodedImageFormat?.Name == "PNG" ||
                               image.Metadata.DecodedImageFormat?.Name == "WEBP" ||
                               image.Metadata.DecodedImageFormat?.Name == "GIF";

        // Calculate new dimensions maintaining aspect ratio
        int targetWidth = image.Width;
        int targetHeight = image.Height;

        if (maxWidth.HasValue && targetWidth > maxWidth.Value)
        {
            var ratio = (float)maxWidth.Value / targetWidth;
            targetWidth = maxWidth.Value;
            targetHeight = (int)(targetHeight * ratio);
        }

        if (maxHeight.HasValue && targetHeight > maxHeight.Value)
        {
            var ratio = (float)maxHeight.Value / targetHeight;
            targetHeight = maxHeight.Value;
            targetWidth = (int)(targetWidth * ratio);
        }

        // Resize if needed
        if (targetWidth != image.Width || targetHeight != image.Height)
        {
            image.Mutate(x => x.Resize(targetWidth, targetHeight, OptimizationResampler));
        }

        // Choose format based on transparency
        using var outputStream = new MemoryStream();

        if (hasTransparency)
        {
            // Preserve transparency with PNG (for icons, logos, etc.)
            image.SaveAsPng(outputStream, new SixLabors.ImageSharp.Formats.Png.PngEncoder
            {
                CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.BestCompression
            });
            isPng = true;
        }
        else
        {
            // Use JPEG for photos (better compression)
            image.SaveAsJpeg(outputStream, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder
            {
                Quality = quality
            });
            isPng = false;
        }

        return outputStream.ToArray();
    }

    /// <summary>
    /// Identifies decoded resource requirements from the downloaded bytes without allocating complete pixel buffers.
    /// </summary>
    internal static DecodedImageResourceResult PreflightDecodedResources(ReadOnlySpan<byte> imageBytes)
    {
        try
        {
            var png = PngFrameAuthority.Inspect(imageBytes);
            if (png.Decision == PngAuthorityDecision.Failed)
            {
                return DecodedImageResourceResult.Failed();
            }

            if (png.Decision == PngAuthorityDecision.TooManyFrames)
            {
                return DecodedImageResourceResult.TooLarge(
                    "frame-count",
                    png.FrameCount,
                    DecodedImageResourceLimits.MaximumFrameCount);
            }

            var webp = WebpFrameAuthority.Inspect(imageBytes);
            if (webp.Decision == WebpAuthorityDecision.Failed)
            {
                return DecodedImageResourceResult.Failed();
            }

            if (webp.Decision == WebpAuthorityDecision.TooManyFrames)
            {
                return DecodedImageResourceResult.TooLarge(
                    "frame-count",
                    webp.FrameCount,
                    DecodedImageResourceLimits.MaximumFrameCount);
            }

            var info = DecodedImageResourceLimits.Identify(imageBytes);
            var formatName = info.Metadata.DecodedImageFormat?.Name;
            var usesFrameSentinel = string.Equals(formatName, "GIF", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(formatName, "WEBP", StringComparison.OrdinalIgnoreCase);
            var frameCount = usesFrameSentinel
                ? Math.Max(1, info.FrameMetadataCollection.Count)
                : 1;

            return DecodedImageResourceLimits.Evaluate(info.Width, info.Height, frameCount);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or
            NotSupportedException or ArgumentException or SixLabors.ImageSharp.Memory.InvalidMemoryOperationException)
        {
            return DecodedImageResourceResult.Failed();
        }
    }
}

/// <summary>Scans bounded PNG chunks only far enough to reject declared animation before decode.</summary>
internal static class PngFrameAuthority
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static PngAuthorityResult Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < PngSignature.Length || !bytes[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return PngAuthorityResult.NotPng();
        }

        var offset = PngSignature.Length;
        var sawHeader = false;
        var sawImageData = false;
        var sawAnimationControl = false;

        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12)
            {
                return PngAuthorityResult.Failed();
            }

            var length = ReadUInt32BigEndian(bytes.Slice(offset, 4));
            long chunkEnd;
            try
            {
                chunkEnd = checked((long)offset + 12L + length);
            }
            catch (OverflowException)
            {
                return PngAuthorityResult.Failed();
            }

            if (chunkEnd > bytes.Length)
            {
                return PngAuthorityResult.Failed();
            }

            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, checked((int)length));
            if (!sawHeader)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13)
                {
                    return PngAuthorityResult.Failed();
                }

                sawHeader = true;
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                return PngAuthorityResult.Failed();
            }

            if (type.SequenceEqual("acTL"u8))
            {
                if (length != 8 || sawAnimationControl || sawImageData ||
                    !HasValidAnimationControlCrc(type, data, bytes.Slice(offset + 16, 4)))
                {
                    return PngAuthorityResult.Failed();
                }

                sawAnimationControl = true;
                var declaredFrames = ReadUInt32BigEndian(data[..4]);
                if (declaredFrames == 0)
                {
                    return PngAuthorityResult.Failed();
                }

                if (declaredFrames > DecodedImageResourceLimits.MaximumFrameCount)
                {
                    return PngAuthorityResult.TooManyFrames(declaredFrames);
                }
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                sawImageData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || chunkEnd != bytes.Length)
                {
                    return PngAuthorityResult.Failed();
                }

                return PngAuthorityResult.StillPng();
            }

            offset = checked((int)chunkEnd);
        }

        return PngAuthorityResult.Failed();
    }

    /// <summary>Validates only the CRC-protected acTL type and data that drive frame policy.</summary>
    private static bool HasValidAnimationControlCrc(
        ReadOnlySpan<byte> type,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> expectedBytes)
    {
        var crc = uint.MaxValue;
        UpdateCrc(ref crc, type);
        UpdateCrc(ref crc, data);
        return ~crc == ReadUInt32BigEndian(expectedBytes);
    }

    private static void UpdateCrc(ref uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) == 0 ? crc >> 1 : 0xEDB88320u ^ (crc >> 1);
            }
        }
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];
}

/// <summary>Classifies the narrow PNG animation-control scan.</summary>
internal enum PngAuthorityDecision
{
    NotPng,
    StillPng,
    TooManyFrames,
    Failed
}

/// <summary>Contains a CRC-trusted excessive APNG frame declaration when present.</summary>
internal readonly record struct PngAuthorityResult(PngAuthorityDecision Decision, long FrameCount)
{
    public static PngAuthorityResult NotPng() => new(PngAuthorityDecision.NotPng, 0);
    public static PngAuthorityResult StillPng() => new(PngAuthorityDecision.StillPng, 1);
    public static PngAuthorityResult TooManyFrames(long frameCount) =>
        new(PngAuthorityDecision.TooManyFrames, frameCount);
    public static PngAuthorityResult Failed() => new(PngAuthorityDecision.Failed, 0);
}
