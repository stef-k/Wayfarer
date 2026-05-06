namespace Wayfarer.LocCheck;

/// <summary>
/// Configuration values used by the LOC checker.
/// </summary>
public sealed class LocCheckOptions
{
    /// <summary>
    /// Gets or sets the repository root directory.
    /// </summary>
    public required string RootPath { get; init; }

    /// <summary>
    /// Gets or sets the baseline file path.
    /// </summary>
    public required string BaselinePath { get; init; }

    /// <summary>
    /// Gets or sets the LOC warning threshold.
    /// </summary>
    public int WarningThreshold { get; init; } = 400;

    /// <summary>
    /// Gets or sets the hard LOC failure threshold.
    /// </summary>
    public int FailureThreshold { get; init; } = 600;

    /// <summary>
    /// Gets or sets a value indicating whether the baseline should be regenerated.
    /// </summary>
    public bool UpdateBaseline { get; init; }
}
