using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Memory;

namespace Wayfarer.Util;

/// <summary>
/// Owns the fixed decoded-resource limits and process-long ImageSharp allocator used by the image proxy.
/// </summary>
internal static class DecodedImageResourceLimits
{
    public const long MaximumWidth = 8_192;
    public const long MaximumHeight = 8_192;
    public const long MaximumPixelsPerFrame = 12_000_000;
    public const long MaximumFrameCount = 8;
    public const long MaximumAggregateDecodedBytes = 64L * 1024 * 1024;
    public const int AllocationGroupLimitMegabytes = 128;
    public const int RetainedPoolMegabytes = 128;
    public const int FrameSentinel = (int)MaximumFrameCount + 1;

    private static readonly Configuration ProxyConfiguration = CreateConfiguration();

    /// <summary>Options that bound GIF/WebP identification at one frame beyond the accepted limit.</summary>
    private static readonly DecoderOptions IdentificationOptions = new()
    {
        Configuration = ProxyConfiguration,
        MaxFrames = FrameSentinel
    };

    /// <summary>Options that read APNG dimensions while the parser supplies declared frame authority.</summary>
    private static readonly DecoderOptions ApngIdentificationOptions = new()
    {
        Configuration = ProxyConfiguration,
        MaxFrames = 1
    };

    /// <summary>Options that decode every frame of an image already accepted by preflight.</summary>
    private static readonly DecoderOptions DecodeOptions = new()
    {
        Configuration = ProxyConfiguration
    };

    /// <summary>Identifies an image with the codec-appropriate immutable proxy options.</summary>
    public static ImageInfo Identify(ReadOnlySpan<byte> bytes, bool isApng) =>
        Image.Identify(isApng ? ApngIdentificationOptions : IdentificationOptions, bytes);

    /// <summary>Loads every frame of an image accepted by decoded-resource preflight.</summary>
    public static Image Load(ReadOnlySpan<byte> bytes) => Image.Load(DecodeOptions, bytes);

    /// <summary>Evaluates independent limits using conservative checked four-byte pixel accounting.</summary>
    public static DecodedImageResourceResult Evaluate(long width, long height, long frameCount)
    {
        if (width <= 0 || height <= 0 || frameCount <= 0)
        {
            return DecodedImageResourceResult.Failed();
        }

        long pixelsPerFrame;
        long aggregateDecodedBytes;
        try
        {
            pixelsPerFrame = checked(width * height);
            aggregateDecodedBytes = checked(pixelsPerFrame * 4L * frameCount);
        }
        catch (OverflowException)
        {
            return DecodedImageResourceResult.TooLarge(
                "resource-arithmetic",
                long.MaxValue,
                MaximumAggregateDecodedBytes);
        }

        if (width > MaximumWidth)
        {
            return DecodedImageResourceResult.TooLarge("width", width, MaximumWidth);
        }

        if (height > MaximumHeight)
        {
            return DecodedImageResourceResult.TooLarge("height", height, MaximumHeight);
        }

        if (pixelsPerFrame > MaximumPixelsPerFrame)
        {
            return DecodedImageResourceResult.TooLarge(
                "pixels-per-frame",
                pixelsPerFrame,
                MaximumPixelsPerFrame);
        }

        if (frameCount > MaximumFrameCount)
        {
            return DecodedImageResourceResult.TooLarge("frame-count", frameCount, MaximumFrameCount);
        }

        if (aggregateDecodedBytes > MaximumAggregateDecodedBytes)
        {
            return DecodedImageResourceResult.TooLarge(
                "aggregate-decoded-bytes",
                aggregateDecodedBytes,
                MaximumAggregateDecodedBytes);
        }

        return DecodedImageResourceResult.Accepted();
    }

    /// <summary>Creates the single dedicated allocator without mutating ImageSharp's global configuration.</summary>
    private static Configuration CreateConfiguration()
    {
        var configuration = Configuration.Default.Clone();
        configuration.MemoryAllocator = MemoryAllocator.Create(new MemoryAllocatorOptions
        {
            AllocationLimitMegabytes = AllocationGroupLimitMegabytes,
            MaximumPoolSizeMegabytes = RetainedPoolMegabytes
        });
        return configuration;
    }
}

/// <summary>Classifies decoded preflight without conflating malformed input with positive policy rejection.</summary>
internal enum DecodedImageResourceDecision
{
    Accepted,
    TooLarge,
    Failed
}

/// <summary>Contains a bounded policy decision and optional numeric rejection evidence.</summary>
internal readonly record struct DecodedImageResourceResult(
    DecodedImageResourceDecision Decision,
    string? LimitName,
    long Observed,
    long Limit)
{
    public static DecodedImageResourceResult Accepted() =>
        new(DecodedImageResourceDecision.Accepted, null, 0, 0);

    public static DecodedImageResourceResult TooLarge(string limitName, long observed, long limit) =>
        new(DecodedImageResourceDecision.TooLarge, limitName, observed, limit);

    public static DecodedImageResourceResult Failed() =>
        new(DecodedImageResourceDecision.Failed, null, 0, 0);
}

/// <summary>Signals a positive decoded-resource policy rejection to the proxy service.</summary>
internal sealed class DecodedImageResourceRejectedException : Exception
{
    public DecodedImageResourceRejectedException(DecodedImageResourceResult result)
        : base("The decoded image exceeds a fixed proxy resource limit.")
    {
        Result = result;
    }

    public DecodedImageResourceResult Result { get; }
}
