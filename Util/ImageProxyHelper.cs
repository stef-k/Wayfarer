using System.Net;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Processing;
using Wayfarer.Services;

namespace Wayfarer.Util;

/// <summary>
/// Shared utility methods for image proxy operations: SSRF validation, cache key
/// computation, and ImageSharp optimization. Used by both TripViewerController
/// (HTTP pipeline) and ImageProxyService (background warm-up).
/// </summary>
public static class ImageProxyHelper
{
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
            image.Mutate(x => x.Resize(targetWidth, targetHeight, KnownResamplers.Lanczos3));
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
            var apng = ApngFrameAuthority.Inspect(imageBytes);
            if (apng.Decision == ApngAuthorityDecision.Failed)
            {
                return DecodedImageResourceResult.Failed();
            }

            var info = DecodedImageResourceLimits.Identify(
                imageBytes,
                apng.Decision == ApngAuthorityDecision.Animated);
            var formatName = info.Metadata.DecodedImageFormat?.Name;
            var usesFrameSentinel = string.Equals(formatName, "GIF", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(formatName, "WEBP", StringComparison.OrdinalIgnoreCase);
            long frameCount = apng.Decision == ApngAuthorityDecision.Animated
                ? apng.FrameCount
                : usesFrameSentinel
                    ? Math.Max(1, info.FrameMetadataCollection.Count)
                    : 1;

            return DecodedImageResourceLimits.Evaluate(info.Width, info.Height, frameCount);
        }
        catch (Exception ex) when (ex is UnknownImageFormatException or InvalidImageContentException or NotSupportedException or ArgumentException)
        {
            return DecodedImageResourceResult.Failed();
        }
    }
}

/// <summary>Reads only the PNG animation-control authority needed to bound APNG frame allocation.</summary>
internal static class ApngFrameAuthority
{
    private static ReadOnlySpan<byte> PngSignature => [137, 80, 78, 71, 13, 10, 26, 10];

    public static ApngAuthorityResult Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < PngSignature.Length || !bytes[..PngSignature.Length].SequenceEqual(PngSignature))
        {
            return ApngAuthorityResult.NotPng();
        }

        var offset = PngSignature.Length;
        var sawHeader = false;
        var sawImageData = false;
        var sawEnd = false;
        var firstFrameControlPrecededImageData = false;
        long? declaredFrames = null;
        long frameControls = 0;
        uint expectedSequence = 0;

        while (offset < bytes.Length)
        {
            if (bytes.Length - offset < 12)
            {
                return ApngAuthorityResult.Failed();
            }

            var length = ReadUInt32BigEndian(bytes.Slice(offset, 4));
            long chunkEnd;
            try
            {
                chunkEnd = checked((long)offset + 12L + length);
            }
            catch (OverflowException)
            {
                return ApngAuthorityResult.Failed();
            }

            if (chunkEnd > bytes.Length)
            {
                return ApngAuthorityResult.Failed();
            }

            var type = bytes.Slice(offset + 4, 4);
            var data = bytes.Slice(offset + 8, checked((int)length));
            if (!sawHeader)
            {
                if (!type.SequenceEqual("IHDR"u8) || length != 13)
                {
                    return ApngAuthorityResult.Failed();
                }

                sawHeader = true;
            }
            else if (type.SequenceEqual("IHDR"u8))
            {
                return ApngAuthorityResult.Failed();
            }

            if (type.SequenceEqual("acTL"u8))
            {
                if (length != 8 || declaredFrames.HasValue || sawImageData)
                {
                    return ApngAuthorityResult.Failed();
                }

                declaredFrames = ReadUInt32BigEndian(data[..4]);
                if (declaredFrames <= 0)
                {
                    return ApngAuthorityResult.Failed();
                }
            }
            else if (type.SequenceEqual("fcTL"u8))
            {
                if (!declaredFrames.HasValue || length != 26 || ReadUInt32BigEndian(data[..4]) != expectedSequence)
                {
                    return ApngAuthorityResult.Failed();
                }

                if (frameControls == 0)
                {
                    firstFrameControlPrecededImageData = !sawImageData;
                }

                expectedSequence++;
                frameControls++;
            }
            else if (type.SequenceEqual("fdAT"u8))
            {
                if (!declaredFrames.HasValue || length < 4 || !sawImageData ||
                    ReadUInt32BigEndian(data[..4]) != expectedSequence)
                {
                    return ApngAuthorityResult.Failed();
                }

                expectedSequence++;
            }
            else if (type.SequenceEqual("IDAT"u8))
            {
                sawImageData = true;
            }
            else if (type.SequenceEqual("IEND"u8))
            {
                if (length != 0 || sawEnd || declaredFrames.HasValue && chunkEnd != bytes.Length)
                {
                    return ApngAuthorityResult.Failed();
                }

                sawEnd = true;
                if (!declaredFrames.HasValue)
                {
                    return ApngAuthorityResult.StaticPng();
                }
            }

            offset = checked((int)chunkEnd);
        }

        if (!sawHeader || !sawEnd)
        {
            return ApngAuthorityResult.Failed();
        }

        if (!declaredFrames.HasValue)
        {
            return ApngAuthorityResult.Failed();
        }

        if (!sawImageData || !firstFrameControlPrecededImageData || frameControls != declaredFrames.Value)
        {
            return ApngAuthorityResult.Failed();
        }

        return ApngAuthorityResult.Animated(declaredFrames.Value);
    }

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> value) =>
        ((uint)value[0] << 24) | ((uint)value[1] << 16) | ((uint)value[2] << 8) | value[3];
}

/// <summary>Classifies the narrow PNG animation-control scan.</summary>
internal enum ApngAuthorityDecision
{
    NotPng,
    StaticPng,
    Animated,
    Failed
}

/// <summary>Contains the APNG-declared frame count when present and structurally valid.</summary>
internal readonly record struct ApngAuthorityResult(ApngAuthorityDecision Decision, long FrameCount)
{
    public static ApngAuthorityResult NotPng() => new(ApngAuthorityDecision.NotPng, 0);
    public static ApngAuthorityResult StaticPng() => new(ApngAuthorityDecision.StaticPng, 1);
    public static ApngAuthorityResult Animated(long frameCount) => new(ApngAuthorityDecision.Animated, frameCount);
    public static ApngAuthorityResult Failed() => new(ApngAuthorityDecision.Failed, 0);
}
