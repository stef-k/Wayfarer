namespace Wayfarer.LocCheck;

/// <summary>
/// Source-controlled baseline for grandfathered existing files.
/// </summary>
public sealed class LocBaseline
{
    /// <summary>
    /// Gets or sets the baseline file format version.
    /// </summary>
    public int Version { get; set; } = 1;

    /// <summary>
    /// Gets or sets the UTC timestamp when the baseline was generated.
    /// </summary>
    public DateTime GeneratedAtUtc { get; set; }

    /// <summary>
    /// Gets or sets the warning threshold used during baseline generation.
    /// </summary>
    public int WarningThreshold { get; set; }

    /// <summary>
    /// Gets or sets the failure threshold used during baseline generation.
    /// </summary>
    public int FailureThreshold { get; set; }

    /// <summary>
    /// Gets or sets file LOC counts keyed by repository-relative path.
    /// </summary>
    public SortedDictionary<string, int> Files { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}
