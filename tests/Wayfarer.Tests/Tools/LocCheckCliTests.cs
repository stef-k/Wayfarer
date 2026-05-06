using System.Diagnostics;
using Xunit;

namespace Wayfarer.Tests.Tools;

/// <summary>
/// Tests for the LOC checker command-line interface.
/// </summary>
public sealed class LocCheckCliTests
{
    /// <summary>
    /// Verifies invalid integer arguments produce a clean usage error instead of a stack trace.
    /// </summary>
    [Fact]
    public async Task Program_ReturnsUsageError_WhenWarnIsNotInteger()
    {
        var result = await RunLocCheckAsync("--warn", "nope");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--warn must be an integer.", result.StandardError);
        Assert.DoesNotContain("Unhandled exception", result.StandardError);
    }

    /// <summary>
    /// Verifies missing option values produce a clean usage error instead of a stack trace.
    /// </summary>
    [Fact]
    public async Task Program_ReturnsUsageError_WhenOptionValueIsMissing()
    {
        var result = await RunLocCheckAsync("--warn");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("Missing value for --warn", result.StandardError);
        Assert.DoesNotContain("Unhandled exception", result.StandardError);
    }

    /// <summary>
    /// Verifies inconsistent thresholds are rejected.
    /// </summary>
    [Fact]
    public async Task Program_ReturnsUsageError_WhenWarnExceedsFail()
    {
        var result = await RunLocCheckAsync("--warn", "10", "--fail", "5");

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("--warn must be less than or equal to --fail.", result.StandardError);
    }

    private static async Task<CliResult> RunLocCheckAsync(params string[] arguments)
    {
        var projectPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "..",
            "tools",
            "Wayfarer.LocCheck",
            "Wayfarer.LocCheck.csproj"));

        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };

        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(projectPath);
        startInfo.ArgumentList.Add("--");
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start dotnet.");
        var standardOutput = await process.StandardOutput.ReadToEndAsync();
        var standardError = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return new CliResult(process.ExitCode, standardOutput, standardError);
    }

    private sealed record CliResult(int ExitCode, string StandardOutput, string StandardError);
}
