using System.Diagnostics;
using Xunit;

namespace Wayfarer.Tests.Tools;

/// <summary>Exercises the built application entrypoint without web/database initialization.</summary>
public sealed class PlaywrightCliTests
{
    /// <summary>Dry-run resolves the bundled Chromium; vendor CLI failure reaches the process exit code.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InstallerExitsBeforeApplicationStartup(bool valid)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Wayfarer.dll"));
        start.ArgumentList.Add("playwright");
        foreach (var argument in valid ? new[] { "install", "chromium", "--dry-run" } : new[] { "invalid-command" })
            start.ArgumentList.Add(argument);
        // Any accidental application startup must fail, rather than touch a developer database.
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Production";
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        start.Environment["ConnectionStrings__DefaultConnection"] = "deliberately invalid";
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        var text = await output + await error;
        if (valid)
        {
            Assert.Equal(0, process.ExitCode);
            Assert.Contains("chromium", text);
        }
        else
        {
            Assert.Equal(1, process.ExitCode);
            Assert.Contains("unknown command", text);
        }
        Assert.DoesNotContain("Application is not prepared", text);
    }
}
