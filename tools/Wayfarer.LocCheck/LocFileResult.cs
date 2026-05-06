namespace Wayfarer.LocCheck;

/// <summary>
/// Result for one checked file.
/// </summary>
public sealed record LocFileResult(
    string Path,
    int Lines,
    int? BaselineLines,
    LocSeverity Severity,
    string Message);

/// <summary>
/// Overall LOC check result.
/// </summary>
public sealed record LocCheckResult(
    IReadOnlyList<LocFileResult> Files,
    bool BaselineUpdated)
{
    /// <summary>
    /// Gets a value indicating whether hard failures were found.
    /// </summary>
    public bool HasFailures => Files.Any(file => file.Severity == LocSeverity.Failure);

    /// <summary>
    /// Gets a value indicating whether warnings were found.
    /// </summary>
    public bool HasWarnings => Files.Any(file => file.Severity == LocSeverity.Warning);
}

/// <summary>
/// Severity for one file result.
/// </summary>
public enum LocSeverity
{
    /// <summary>
    /// File is within policy.
    /// </summary>
    Ok,

    /// <summary>
    /// File exceeds warning threshold but not hard policy.
    /// </summary>
    Warning,

    /// <summary>
    /// File violates hard policy.
    /// </summary>
    Failure
}
