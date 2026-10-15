namespace Wayfarer.Util;

/// <summary>
/// Owns the fixed decoded-resource limits mirrored by the contained image helper.
/// </summary>
internal static class DecodedImageResourceLimits
{
    public const long MaximumWidth = 8_192;
    public const long MaximumHeight = 8_192;
    public const long MaximumPixelsPerFrame = 12_000_000;
    public const long MaximumFrameCount = 1;
    public const long MaximumDecodedBytes = 64L * 1024 * 1024;
    /// <summary>Evaluates independent limits using conservative checked four-byte pixel accounting.</summary>
    public static DecodedImageResourceResult Evaluate(long width, long height, long frameCount)
    {
        if (width <= 0 || height <= 0 || frameCount <= 0)
        {
            return DecodedImageResourceResult.Failed();
        }

        long pixelsPerFrame;
        long decodedBytes;
        try
        {
            pixelsPerFrame = checked(width * height);
            decodedBytes = checked(pixelsPerFrame * 4L);
        }
        catch (OverflowException)
        {
            return DecodedImageResourceResult.TooLarge(
                "resource-arithmetic",
                long.MaxValue,
                MaximumDecodedBytes);
        }

        if (width > MaximumWidth)
        {
            return DecodedImageResourceResult.TooLarge("width", width, MaximumWidth);
        }

        if (height > MaximumHeight)
        {
            return DecodedImageResourceResult.TooLarge("height", height, MaximumHeight);
        }

        if (frameCount > MaximumFrameCount)
        {
            return DecodedImageResourceResult.TooLarge("frame-count", frameCount, MaximumFrameCount);
        }

        return EvaluateCalculated(width, height, pixelsPerFrame, decodedBytes, frameCount);
    }

    /// <summary>Applies every independent boundary to already checked resource values.</summary>
    internal static DecodedImageResourceResult EvaluateCalculated(
        long width,
        long height,
        long pixels,
        long decodedBytes,
        long frameCount)
    {
        if (width <= 0 || height <= 0 || pixels <= 0 || decodedBytes <= 0 || frameCount <= 0)
        {
            return DecodedImageResourceResult.Failed();
        }

        if (width > MaximumWidth)
        {
            return DecodedImageResourceResult.TooLarge("width", width, MaximumWidth);
        }

        if (height > MaximumHeight)
        {
            return DecodedImageResourceResult.TooLarge("height", height, MaximumHeight);
        }

        if (pixels > MaximumPixelsPerFrame)
        {
            return DecodedImageResourceResult.TooLarge("pixels", pixels, MaximumPixelsPerFrame);
        }

        if (decodedBytes > MaximumDecodedBytes)
        {
            return DecodedImageResourceResult.TooLarge(
                "decoded-bytes",
                decodedBytes,
                MaximumDecodedBytes);
        }

        if (frameCount > MaximumFrameCount)
        {
            return DecodedImageResourceResult.TooLarge("frame-count", frameCount, MaximumFrameCount);
        }

        return DecodedImageResourceResult.Accepted();
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
