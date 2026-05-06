namespace Wayfarer.LocCheck;

/// <summary>
/// Runs LOC checks and baseline updates.
/// </summary>
public sealed class LocCheckRunner
{
    private readonly SourceFileScanner scanner;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocCheckRunner"/> class.
    /// </summary>
    public LocCheckRunner(SourceFileScanner scanner)
    {
        this.scanner = scanner;
    }

    /// <summary>
    /// Executes a LOC check using the supplied options.
    /// </summary>
    public LocCheckResult Run(LocCheckOptions options)
    {
        var files = scanner.GetSourceFiles(options.RootPath);
        var counts = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in files)
        {
            counts[SourceFileScanner.ToRelativePath(options.RootPath, path)] = LineCounter.CountNonBlankLines(path);
        }

        if (options.UpdateBaseline)
        {
            var baseline = new LocBaseline
            {
                GeneratedAtUtc = DateTime.UtcNow,
                WarningThreshold = options.WarningThreshold,
                FailureThreshold = options.FailureThreshold,
                Files = counts
            };

            LocBaselineStore.Save(options.BaselinePath, baseline);
            return new LocCheckResult([], BaselineUpdated: true);
        }

        var existingBaseline = LocBaselineStore.Load(options.BaselinePath);
        var results = counts
            .Select(pair => Evaluate(pair.Key, pair.Value, existingBaseline, options))
            .Where(result => result.Severity != LocSeverity.Ok)
            .OrderByDescending(result => result.Severity)
            .ThenByDescending(result => result.Lines)
            .ThenBy(result => result.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new LocCheckResult(results, BaselineUpdated: false);
    }

    private static LocFileResult Evaluate(
        string path,
        int lines,
        LocBaseline baseline,
        LocCheckOptions options)
    {
        if (baseline.Files.TryGetValue(path, out var baselineLines))
        {
            if (lines > baselineLines && lines >= options.WarningThreshold)
            {
                return new LocFileResult(
                    path,
                    lines,
                    baselineLines,
                    LocSeverity.Failure,
                    $"grew past baseline while at or above warning threshold ({lines} > {baselineLines})");
            }

            return new LocFileResult(path, lines, baselineLines, LocSeverity.Ok, "ok");
        }

        if (lines > options.FailureThreshold)
        {
            return new LocFileResult(
                path,
                lines,
                null,
                LocSeverity.Failure,
                $"new file exceeds hard cap ({lines} > {options.FailureThreshold})");
        }

        if (lines >= options.WarningThreshold)
        {
            return new LocFileResult(
                path,
                lines,
                null,
                LocSeverity.Warning,
                $"new file exceeds warning threshold ({lines} >= {options.WarningThreshold})");
        }

        return new LocFileResult(path, lines, null, LocSeverity.Ok, "ok");
    }
}
