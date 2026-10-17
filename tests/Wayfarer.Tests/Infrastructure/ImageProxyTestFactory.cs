using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Services;
using Wayfarer.Parsers;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Creates actual scoped proxy workers and small valid rasters for pipeline tests.</summary>
internal static class ImageProxyTestFactory
{
    /// <summary>Mirrors production scope ownership while keeping transport and cache controllable.</summary>
    internal static IServiceScopeFactory ScopeFactory(HttpClient client, IProxiedImageCacheService cache,
        IApplicationSettingsService settings, ILogger<ImageProxyService>? logger = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IImageProxyService>(sp => new ImageProxyService(client, cache, settings,
            sp.GetRequiredService<IServiceScopeFactory>(), logger ?? NullLogger<ImageProxyService>.Instance));
        return services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
    }

    /// <summary>Reads fresh copies of the original valid and rejected-format 2x2 golden rasters.</summary>
    internal static byte[] Raster(string format = "jpeg") => format switch
    {
        "jpeg" or "png" or "gif" or "webp" or "bmp" or "tiff" or "pbm" or "tga" or "qoi" =>
            Fixture($"raster.{format}"),
        _ => throw new ArgumentException("Unknown fixture format.", nameof(format))
    };

    /// <summary>Loads the documented test-only corpus copied beside the test assembly.</summary>
    internal static byte[] Fixture(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Imaging", name));
}
