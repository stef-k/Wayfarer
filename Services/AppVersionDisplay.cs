namespace Wayfarer.Services;

/// <summary>
/// Formats user-visible Wayfarer version text.
/// </summary>
public static class AppVersionDisplay
{
    /// <summary>
    /// Formats the shared footer version text.
    /// </summary>
    /// <param name="appVersionProvider">The provider for the compiled application version.</param>
    /// <returns>The footer version string.</returns>
    public static string FooterText(IAppVersionProvider appVersionProvider)
    {
        return $"Wayfarer v{appVersionProvider.Version}";
    }
}
