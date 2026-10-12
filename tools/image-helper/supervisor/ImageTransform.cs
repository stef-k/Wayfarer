namespace Wayfarer.ImagePrototype;

/// <summary>The complete trusted recipe; the 30-second limit is capped by the origin budget.</summary>
public sealed record ImageTransform(
    int Width = 600,
    int Height = 8192,
    int Quality = 85,
    string OutputFormat = "auto",
    int MaximumInputBytes = 50 * 1024 * 1024,
    int MaximumOutputBytes = 50 * 1024 * 1024);

/// <summary>The future HTTP owner maps TooLarge to 400, Failed to 404 and Unavailable to 503.</summary>
public enum ImageTransformStatus { Success, TooLarge, Failed, Unavailable }

/// <summary>Only success carries image bytes. Resource observations cover this child alone.</summary>
public sealed record ImageTransformResult(
    ImageTransformStatus Status,
    byte[]? Bytes = null,
    string? MimeType = null,
    int? ExitCode = null,
    int? ProcessId = null,
    double? CpuMilliseconds = null,
    long? PeakRssBytes = null);
