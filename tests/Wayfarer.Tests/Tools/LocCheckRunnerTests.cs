using Wayfarer.LocCheck;
using Xunit;

namespace Wayfarer.Tests.Tools;

/// <summary>
/// Tests for the repository LOC checker.
/// </summary>
public sealed class LocCheckRunnerTests : IDisposable
{
    private readonly string rootPath;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocCheckRunnerTests"/> class.
    /// </summary>
    public LocCheckRunnerTests()
    {
        rootPath = Path.Combine(Path.GetTempPath(), $"wayfarer-loc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Directory.Exists(rootPath))
        {
            Directory.Delete(rootPath, recursive: true);
        }
    }

    /// <summary>
    /// Verifies that a new source file below the warning threshold passes.
    /// </summary>
    [Fact]
    public void Run_AllowsNewFileBelowWarningThreshold()
    {
        WriteLines("NewFile.cs", 3);

        var result = RunCheck(warn: 4, fail: 6);

        Assert.False(result.HasWarnings);
        Assert.False(result.HasFailures);
    }

    /// <summary>
    /// Verifies that a new source file above the warning threshold is reported.
    /// </summary>
    [Fact]
    public void Run_WarnsForNewFileAboveWarningThreshold()
    {
        WriteLines("NewFile.cs", 4);

        var result = RunCheck(warn: 4, fail: 6);

        var warning = Assert.Single(result.Files);
        Assert.Equal(LocSeverity.Warning, warning.Severity);
        Assert.Equal("NewFile.cs", warning.Path);
    }

    /// <summary>
    /// Verifies that a new source file above the hard cap fails.
    /// </summary>
    [Fact]
    public void Run_FailsForNewFileAboveFailureThreshold()
    {
        WriteLines("NewFile.cs", 7);

        var result = RunCheck(warn: 4, fail: 6);

        var failure = Assert.Single(result.Files);
        Assert.Equal(LocSeverity.Failure, failure.Severity);
    }

    /// <summary>
    /// Verifies that legacy files over the cap are grandfathered by baseline.
    /// </summary>
    [Fact]
    public void Run_AllowsBaselineFileAboveFailureThresholdWhenUnchanged()
    {
        WriteLines("Legacy.cs", 8);
        SaveBaseline(("Legacy.cs", 8));

        var result = RunCheck(warn: 4, fail: 6);

        Assert.Empty(result.Files);
    }

    /// <summary>
    /// Verifies that a legacy oversized file cannot grow past its baseline.
    /// </summary>
    [Fact]
    public void Run_FailsWhenBaselineFileGrowsPastBaseline()
    {
        WriteLines("Legacy.cs", 9);
        SaveBaseline(("Legacy.cs", 8));

        var result = RunCheck(warn: 4, fail: 6);

        var failure = Assert.Single(result.Files);
        Assert.Equal(LocSeverity.Failure, failure.Severity);
    }

    /// <summary>
    /// Verifies that a baseline file under the cap still cannot grow when it is above the warning threshold.
    /// </summary>
    [Fact]
    public void Run_FailsWhenBaselineFileUnderCapGrowsAboveWarningThreshold()
    {
        WriteLines("Legacy.cs", 5);
        SaveBaseline(("Legacy.cs", 4));

        var result = RunCheck(warn: 4, fail: 6);

        var failure = Assert.Single(result.Files);
        Assert.Equal(LocSeverity.Failure, failure.Severity);
    }

    /// <summary>
    /// Verifies that generated and vendor paths are ignored.
    /// </summary>
    [Fact]
    public void Run_IgnoresExcludedFilesAndPaths()
    {
        WriteLines(".local/publish/wwwroot/lib/vendor.js", 100);
        WriteLines("Migrations/Generated.cs", 100);
        WriteLines("wwwroot/lib/vendor.js", 100);
        WriteLines("wwwroot/dist/app.js", 100);
        WriteLines("Feature.Designer.cs", 100);

        var result = RunCheck(warn: 4, fail: 6);

        Assert.Empty(result.Files);
    }

    /// <summary>
    /// Verifies that runtime folder exclusions are root-relative and do not hide future source folders.
    /// </summary>
    [Fact]
    public void Run_DoesNotExcludeRuntimeDirectoryNamesWhenNestedUnderSource()
    {
        WriteLines("Features/Uploads/UploadService.cs", 7);

        var result = RunCheck(warn: 4, fail: 6);

        var failure = Assert.Single(result.Files);
        Assert.Equal(LocSeverity.Failure, failure.Severity);
        Assert.Equal("Features/Uploads/UploadService.cs", failure.Path);
    }

    private LocCheckResult RunCheck(int warn, int fail)
    {
        var options = new LocCheckOptions
        {
            RootPath = rootPath,
            BaselinePath = Path.Combine(rootPath, "baseline.json"),
            WarningThreshold = warn,
            FailureThreshold = fail
        };

        return new LocCheckRunner(new SourceFileScanner()).Run(options);
    }

    private void SaveBaseline(params (string Path, int Lines)[] files)
    {
        var baseline = new LocBaseline
        {
            GeneratedAtUtc = DateTime.UtcNow,
            WarningThreshold = 4,
            FailureThreshold = 6
        };

        foreach (var file in files)
        {
            baseline.Files[file.Path] = file.Lines;
        }

        LocBaselineStore.Save(Path.Combine(rootPath, "baseline.json"), baseline);
    }

    private void WriteLines(string relativePath, int lineCount)
    {
        var path = Path.Combine(rootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, Enumerable.Range(1, lineCount).Select(index => $"line {index}"));
    }
}
