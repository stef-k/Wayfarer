using Wayfarer.Services;

namespace Wayfarer.CommandLine;

/// <summary>
/// Handles app CLI commands that do not require web host startup.
/// </summary>
public static class AppVersionCli
{
    /// <summary>
    /// Handles supported app CLI commands when present.
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

        if (args.Length == 0)
        {
            return false;
        }

        if (args.Length == 1 && (IsHelpCommand(args[0]) || IsHelpOption(args[0])))
        {
            output.WriteLine(TopLevelHelp);
            return true;
        }

        if (string.Equals(args[0], "version", StringComparison.OrdinalIgnoreCase))
        {
            if (args.Length == 2 && IsHelpOption(args[1]))
            {
                output.WriteLine(VersionHelp);
                return true;
            }

            output.WriteLine($"Wayfarer {appVersionProvider.Version}");
            return true;
        }

        if (args.Length == 2
            && string.Equals(args[0], "reset-password", StringComparison.OrdinalIgnoreCase)
            && IsHelpOption(args[1]))
        {
            output.WriteLine(ResetPasswordHelp);
            return true;
        }

        return false;
    }

    private const string TopLevelHelp = """
        Wayfarer CLI

        Usage:
          Wayfarer <command> [options]

        Commands:
          version                         Print the compiled Wayfarer version.
          reset-password <user> <pass>    Reset a user's password.
          help                            Show this help text.
        """;

    private const string VersionHelp = """
        Usage:
          Wayfarer version

        Prints the compiled Wayfarer version and exits before web host startup.
        """;

    private const string ResetPasswordHelp = """
        Usage:
          Wayfarer reset-password <user> <pass>

        Resets a user's password using the configured application database.
        """;

    private static bool IsHelpCommand(string value)
    {
        return string.Equals(value, "help", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHelpOption(string value)
    {
        return string.Equals(value, "--help", StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "-h", StringComparison.OrdinalIgnoreCase);
    }
}
