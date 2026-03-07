using System.Net;
using System.Security.Cryptography;
using System.Text;
using SixLabors.ImageSharp;
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
    /// Maximum response body size in bytes for proxied images (20 MB).
    /// Prevents OOM from attacker-supplied URLs pointing to very large files.
    /// Shared by TripViewerController and ImageProxyService.
    /// </summary>
    public const long MaxProxyImageBytes = 20 * 1024 * 1024;

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
    /// Optimizes an image using ImageSharp: resize and compress while maintaining quality.
    /// Preserves PNG transparency for icons, converts photos to JPEG.
    /// Uses pure managed code with no native dependencies for cross-platform support.
    /// </summary>
    public static byte[] OptimizeImage(byte[] imageBytes, int? maxWidth, int? maxHeight, int quality, out bool isPng)
    {
        using var inputStream = new MemoryStream(imageBytes);
        using var image = Image.Load(inputStream);

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
}
