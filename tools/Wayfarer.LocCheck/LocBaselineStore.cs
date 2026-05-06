using System.Text.Json;

namespace Wayfarer.LocCheck;

/// <summary>
/// Reads and writes LOC baseline files.
/// </summary>
public static class LocBaselineStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    /// <summary>
    /// Loads a baseline from disk, returning an empty baseline when the file is absent.
    /// </summary>
    public static LocBaseline Load(string path)
    {
        if (!File.Exists(path))
        {
            return new LocBaseline();
        }

        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<LocBaseline>(stream, JsonOptions) ?? new LocBaseline();
    }

    /// <summary>
    /// Writes the baseline to disk.
    /// </summary>
    public static void Save(string path, LocBaseline baseline)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var stream = File.Create(path);
        JsonSerializer.Serialize(stream, baseline, JsonOptions);
    }
}
