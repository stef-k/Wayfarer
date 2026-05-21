using System.Reflection;

namespace Wayfarer.Services;

/// <summary>
/// Provides the compiled Wayfarer application version.
/// </summary>
public interface IAppVersionProvider
{
    /// <summary>
    /// Gets the application release version from assembly informational metadata.
    /// </summary>
    string Version { get; }
}

/// <summary>
/// Reads the Wayfarer application version from assembly informational metadata.
/// </summary>
public sealed class AppVersionProvider : IAppVersionProvider
{
    private readonly Assembly _assembly;
    private string? _version;

    /// <summary>
    /// Creates a provider for the Wayfarer application assembly.
    /// </summary>
    public AppVersionProvider()
        : this(typeof(AppVersionProvider).Assembly)
    {
    }

    /// <summary>
    /// Creates a provider for the assembly that contains the supplied marker type.
    /// </summary>
    /// <param name="markerType">A type from the assembly whose metadata should be read.</param>
    public AppVersionProvider(Type markerType)
        : this(markerType.Assembly)
    {
    }

    /// <summary>
    /// Creates a provider for the supplied assembly.
    /// </summary>
    /// <param name="assembly">The assembly whose informational version should be read.</param>
    public AppVersionProvider(Assembly assembly)
    {
        _assembly = assembly;
    }

    /// <inheritdoc />
    public string Version => _version ??= ReadInformationalVersion(_assembly);

    private static string ReadInformationalVersion(Assembly assembly)
    {
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString()
            ?? "0.0.0";
    }
}
