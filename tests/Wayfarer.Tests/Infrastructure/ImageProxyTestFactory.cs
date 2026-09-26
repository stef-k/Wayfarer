using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
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

    /// <summary>Encodes real pixels; magic bytes alone are not a valid image fixture.</summary>
    internal static byte[] Raster(string format = "jpeg")
    {
        using var image = new Image<Rgba32>(2, 2);
        using var stream = new MemoryStream();
        switch (format)
        {
            case "jpeg": image.SaveAsJpeg(stream); break;
            case "png": image.SaveAsPng(stream); break;
            case "gif": image.SaveAsGif(stream); break;
            case "webp": image.SaveAsWebp(stream); break;
            case "bmp": image.SaveAsBmp(stream); break;
            case "tiff": image.SaveAsTiff(stream); break;
            case "pbm": image.SaveAsPbm(stream); break;
            case "tga": image.SaveAsTga(stream); break;
            case "qoi": image.SaveAsQoi(stream); break;
            default: throw new ArgumentException("Unknown fixture format.", nameof(format));
        }
        return stream.ToArray();
    }
}
