namespace Wayfarer.Util;

/// <summary>Resolved CLI recipe; web defaults are resolved at this process boundary.</summary>
public sealed record ImageTransform
{
    /// <summary>Bound both absent axes to 2048; an explicit single axis leaves the other at the safety ceiling.
    /// Explicit 600/85 PDF and fully resolved high-fidelity recipes retain their caller-supplied values.</summary>
    public ImageTransform(int? Width = null, int? Height = null, int? Quality = null,
        string OutputFormat = "auto", int MaximumInputBytes = 50 * 1024 * 1024,
        int MaximumOutputBytes = 50 * 1024 * 1024)
    {
        // Sources are already bounded to 8192: larger caller bounds are equivalent and must still succeed.
        this.Width = Math.Min(Width ?? (Height.HasValue ? 8192 : 2048), 8192);
        this.Height = Math.Min(Height ?? (Width.HasValue ? 8192 : 2048), 8192);
        this.Quality = Quality ?? 90;
        this.OutputFormat = OutputFormat;
        this.MaximumInputBytes = MaximumInputBytes;
        this.MaximumOutputBytes = MaximumOutputBytes;
    }

    /// <summary>Explicit maximum output width, subject to no-upscale geometry and mandatory ceilings.</summary>
    public int Width { get; init; }
    /// <summary>Explicit maximum output height, subject to no-upscale geometry and mandatory ceilings.</summary>
    public int Height { get; init; }
    /// <summary>JPEG encoder quality; PNG remains lossless.</summary>
    public int Quality { get; init; }
    /// <summary>Only auto, jpeg and png are valid.</summary>
    public string OutputFormat { get; init; }
    /// <summary>Effective configured stdin byte budget, never greater than the 200 MiB admin ceiling.</summary>
    public int MaximumInputBytes { get; init; }
    /// <summary>Bounded verified stdout byte budget, never greater than 50 MiB.</summary>
    public int MaximumOutputBytes { get; init; }
}

/// <summary>The HTTP owner maps TooLarge to 400, Failed to 404 and Unavailable to 503.</summary>
public enum ImageTransformStatus { Success, TooLarge, Failed, Unavailable }

/// <summary>Only success carries image bytes; process identity supports independent cleanup checks.</summary>
public sealed record ImageTransformResult(
    ImageTransformStatus Status,
    byte[]? Bytes = null,
    string? MimeType = null,
    int? ExitCode = null,
    int? ProcessId = null);
