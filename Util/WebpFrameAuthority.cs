namespace Wayfarer.Util;

/// <summary>Scans a bounded RIFF/WebP container only far enough to establish still-image frame authority.</summary>
internal static class WebpFrameAuthority
{
    private const int ContainerHeaderLength = 12;
    private const int ChunkHeaderLength = 8;

    /// <summary>Counts only fully bounded ANMF chunks and fails closed on malformed RIFF structure.</summary>
    public static WebpAuthorityResult Inspect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 4 || !bytes[..4].SequenceEqual("RIFF"u8))
        {
            return WebpAuthorityResult.NotWebp();
        }

        if (bytes.Length < ContainerHeaderLength || !bytes.Slice(8, 4).SequenceEqual("WEBP"u8))
        {
            return WebpAuthorityResult.Failed();
        }

        var declaredLength = ReadUInt32LittleEndian(bytes.Slice(4, 4));
        long declaredExtent;
        try
        {
            declaredExtent = checked(8L + declaredLength);
        }
        catch (OverflowException)
        {
            return WebpAuthorityResult.Failed();
        }

        if (declaredExtent != bytes.Length)
        {
            return WebpAuthorityResult.Failed();
        }

        var offset = ContainerHeaderLength;
        var frameCount = 0L;
        while (offset < declaredExtent)
        {
            if (declaredExtent - offset < ChunkHeaderLength)
            {
                return WebpAuthorityResult.Failed();
            }

            var payloadLength = ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            int chunkEnd;
            try
            {
                var paddedPayloadLength = checked((int)payloadLength + (int)(payloadLength & 1));
                chunkEnd = checked(offset + ChunkHeaderLength + paddedPayloadLength);
            }
            catch (OverflowException)
            {
                return WebpAuthorityResult.Failed();
            }

            if (chunkEnd > declaredExtent)
            {
                return WebpAuthorityResult.Failed();
            }

            if (bytes.Slice(offset, 4).SequenceEqual("ANMF"u8))
            {
                frameCount++;
                if (frameCount > DecodedImageResourceLimits.MaximumFrameCount)
                {
                    return WebpAuthorityResult.TooManyFrames(frameCount);
                }
            }

            offset = chunkEnd;
        }

        return WebpAuthorityResult.Webp(frameCount);
    }

    private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> value) =>
        value[0] | ((uint)value[1] << 8) | ((uint)value[2] << 16) | ((uint)value[3] << 24);
}

/// <summary>Classifies the narrow WebP RIFF scan.</summary>
internal enum WebpAuthorityDecision
{
    NotWebp,
    Webp,
    TooManyFrames,
    Failed
}

/// <summary>Contains the bounded WebP frame count when the container is authoritative.</summary>
internal readonly record struct WebpAuthorityResult(WebpAuthorityDecision Decision, long FrameCount)
{
    public static WebpAuthorityResult NotWebp() => new(WebpAuthorityDecision.NotWebp, 0);
    public static WebpAuthorityResult Webp(long frameCount) => new(WebpAuthorityDecision.Webp, frameCount);
    public static WebpAuthorityResult TooManyFrames(long frameCount) =>
        new(WebpAuthorityDecision.TooManyFrames, frameCount);
    public static WebpAuthorityResult Failed() => new(WebpAuthorityDecision.Failed, 0);
}
