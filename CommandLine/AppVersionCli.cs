using Wayfarer.Services;

namespace Wayfarer.CommandLine;

/// <summary>
/// Handles app-version CLI commands that do not require web host startup.
/// </summary>
public static class AppVersionCli
{
    /// <summary>
    /// Handles the version command when present.
    /// </summary>
    /// <param name="args">The process arguments.</param>
    /// <param name="appVersionProvider">The provider for the compiled application version.</param>
    /// <param name="output">The writer used for command output.</param>
    /// <param name="error">The writer used for command errors.</param>
    /// <param name="exitCode">The command exit code when handled.</param>
    /// <returns>True when this handler consumed the command; otherwise false.</returns>
    public static bool TryHandle(
        string[] args,
        IAppVersionProvider appVersionProvider,
        TextWriter output,
        TextWriter error,
        out int exitCode)
    {
        _ = error;
        exitCode = 0;

        if (args.Length == 0 || !string.Equals(args[0], "version", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        output.WriteLine($"Wayfarer {appVersionProvider.Version}");
        return true;
    }
}
