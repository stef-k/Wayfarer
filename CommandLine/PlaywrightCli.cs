namespace Wayfarer.CommandLine;

/// <summary>Exposes the bundled Playwright installation API before application startup.</summary>
internal static class PlaywrightCli
{
    /// <summary>
    /// Forwards explicit Playwright CLI arguments unchanged, including install-deps and --with-deps.
    /// The bundled package owns browser revisions, discovery environment and tool exit status.
    /// </summary>
    internal static bool TryHandle(string[] args, out int exitCode)
    {
        exitCode = 0;
        if (args.Length == 0 || args[0] != "playwright")
            return false;

        exitCode = Microsoft.Playwright.Program.Main(args[1..]);
        return true;
    }
}
