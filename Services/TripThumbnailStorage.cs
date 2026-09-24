using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.FileProviders;

namespace Wayfarer.Services;

/// <summary>Owns generated trip JPEG identity, external storage and the stable public namespace.</summary>
public sealed class TripThumbnailStorage(StoragePaths paths)
{
    /// <summary>Authoritative public branch; misses must never reach legacy webroot files.</summary>
    public const string RequestPath = "/thumbs";
    private const string TripPrefix = RequestPath + "/trips/";

    /// <summary>Current generated trip directory, independent of the application tree.</summary>
    public string Root { get; } = Path.Combine(paths.Thumbnails, "trips");

    /// <summary>Creates the existing hyphenated GUID and positive-dimension JPEG identity.</summary>
    public static string FileName(Guid tripId, int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        return FormattableString.Invariant($"{tripId:D}-{width}x{height}.jpg");
    }

    /// <summary>Accepts only canonical generated filenames, never paths or alternate formats.</summary>
    public static bool TryParse(string? name, out Guid tripId)
    {
        tripId = default;
        if (name == null) return false;
        var match = Regex.Match(name, @"\A([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})-([1-9][0-9]*)x([1-9][0-9]*)\.jpg\z");
        return match.Success && Guid.TryParseExact(match.Groups[1].Value, "D", out tripId) &&
            int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _) &&
            int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>Resolves only a generated filename beneath the current trip root.</summary>
    public string Resolve(string name) => TryParse(name, out _)
        ? StoragePaths.ResolveFile(Root, name)
        : throw new ArgumentException("Invalid generated thumbnail filename.", nameof(name));

    /// <summary>Preserves the existing public URL and update-tick cache version.</summary>
    public string PublicUrl(Guid tripId, int width, int height, DateTime updatedAt) =>
        TripPrefix + FileName(tripId, width, height) + "?v=" + updatedAt.Ticks.ToString(CultureInfo.InvariantCulture);

    /// <summary>Strips cache-busting query data while rejecting all non-thumbnail namespaces and paths.</summary>
    public bool TryResolvePublicUrl(string? url, out string path)
    {
        path = "";
        if (url == null || !url.StartsWith(TripPrefix, StringComparison.Ordinal)) return false;
        var name = url[TripPrefix.Length..].Split('?', 2)[0];
        if (!TryParse(name, out _)) return false;
        path = Resolve(name);
        return true;
    }

    /// <summary>Serves external thumbnails with a terminal 404 and the existing versioned image cache policy.</summary>
    public void MapStaticFiles(IApplicationBuilder app)
    {
        Directory.CreateDirectory(Root);
        var provider = new PhysicalFileProvider(paths.Thumbnails);
        app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>().ApplicationStopped.Register(provider.Dispose);
        app.Map(RequestPath, branch =>
        {
            branch.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = provider,
                OnPrepareResponse = context => context.Context.Response.Headers.CacheControl =
                    "public,max-age=2592000,immutable"
            });
            branch.Run(context =>
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return Task.CompletedTask;
            });
        });
    }
}
