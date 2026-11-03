using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Page entries must include shared imported CSS without duplicate styles or cyclic traversal.</summary>
public sealed class ViteAssetsTests
{
    /// <summary>Missing roots/manifests are explicit unavailable assets, including minimal Razor test hosts.</summary>
    [Fact]
    public void MissingAssetsReturnNoEntry() => Assert.Null(ViteAssets.Read(null, "entry").Script);

    /// <summary>Built Razor loading follows the actual manifest graph rather than only direct entry styles.</summary>
    [Fact]
    public void EntryIncludesTransitiveCssOnce()
    {
        var root = Path.Combine(Path.GetTempPath(), "wayfarer-vite-" + Guid.NewGuid());
        var output = Path.Combine(root, "vite", "trip-editor");
        Directory.CreateDirectory(output);
        try
        {
            File.WriteAllText(Path.Combine(output, "manifest.json"), """
                {"entry":{"file":"entry.js","imports":["shared"],"css":["entry.css","shared.css"]},
                 "shared":{"file":"shared.js","imports":["entry"],"css":["shared.css"]}}
                """);
            var assets = ViteAssets.Read(root, "entry");
            Assert.Equal("/vite/trip-editor/entry.js", assets.Script);
            Assert.Equal(new[] { "/vite/trip-editor/shared.css", "/vite/trip-editor/entry.css" }, assets.Css);
            Assert.Null(ViteAssets.Read(root, "missing").Script);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
