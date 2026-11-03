using System.Text.Json;

namespace Wayfarer.Util;

/// <summary>Resolves page entry scripts and transitive imported CSS from the existing Vite output.</summary>
public static class ViteAssets
{
    /// <summary>Visits imported chunks once so shared styles are included for every consuming Razor entry.</summary>
    public static (string? Script, IReadOnlyList<string> Css) Read(string webRoot, string entryScript)
    {
        var path = Path.Combine(webRoot, "vite", "trip-editor", "manifest.json");
        if (!File.Exists(path)) return (null, Array.Empty<string>());
        using var manifest = JsonDocument.Parse(File.ReadAllText(path));
        var root = manifest.RootElement;
        if (!root.TryGetProperty(entryScript, out var entry)) return (null, Array.Empty<string>());
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var css = new List<string>();
        Visit(entryScript);
        return ("/vite/trip-editor/" + entry.GetProperty("file").GetString(), css);

        // Manifest paths are generated build artifacts; this helper never reads user-provided filesystem paths.
        void Visit(string key)
        {
            if (!visited.Add(key) || !root.TryGetProperty(key, out var chunk)) return;
            if (chunk.TryGetProperty("imports", out var imports))
                foreach (var import in imports.EnumerateArray()) Visit(import.GetString()!);
            if (chunk.TryGetProperty("css", out var styles))
                foreach (var style in styles.EnumerateArray())
                {
                    var href = "/vite/trip-editor/" + style.GetString();
                    if (!css.Contains(href)) css.Add(href);
                }
        }
    }
}
