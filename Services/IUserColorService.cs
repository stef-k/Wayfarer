namespace Wayfarer.Services;

/// <summary>
/// Provides deterministic color assignments for users.
/// </summary>
public interface IUserColorService
{
    /// <summary>
    /// Returns a stable hex color (#RRGGBB) for the provided identifier.
    /// </summary>
    string GetColorHex(string key);
}
