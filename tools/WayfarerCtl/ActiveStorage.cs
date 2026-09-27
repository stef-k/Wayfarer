using System.Text.Json;
using System.Text.RegularExpressions;

namespace WayfarerCtl;

/// <summary>Single local authority for paired database, durable files and rebuildable cache volumes.</summary>
public static class ActiveStorage
{
    private static readonly string[] Generated = ["db-data", "app-data", "app-cache"];
    private static readonly string[] Fixed = ["app-logs", "caddy-data", "caddy-config"];

    /// <summary>Older schemas always select canonical storage; schema three requires a generated UUID.</summary>
    public static void Validate(Deployment config)
    {
        if (config.Schema == 3 ? !Regex.IsMatch(config.StorageGeneration ?? "", "^[a-f0-9]{32}$") : config.StorageGeneration is not null)
            throw new UsageException("Invalid active storage generation for installation schema.");
    }

    /// <summary>Only fixed product volume roles may be resolved; archive strings never name volumes.</summary>
    public static string Volume(Deployment config, string role)
    {
        Validate(config);
        if (!Generated.Contains(role) && !Fixed.Contains(role)) throw new UsageException("Unknown storage role.");
        return config.Project + "_" + role + (Generated.Contains(role) && config.StorageGeneration is not null
            ? "_" + config.StorageGeneration : "");
    }

    /// <summary>Immutable generation files allow the installation pointer alone to commit all three volumes.</summary>
    public static string OverlayPath(string root, Deployment config) =>
        Path.Combine(root, "storage-generations", config.StorageGeneration ?? throw new UsageException("No active generation."), "compose.json");

    /// <summary>Override logical volume names centrally so backup mounts consume the same active data.</summary>
    public static string Render(Deployment config)
    {
        Validate(config);
        if (config.StorageGeneration is null) throw new UsageException("No active generation.");
        return JsonSerializer.Serialize(new { volumes = Generated.ToDictionary(role => role,
            role => new { name = Volume(config, role), external = true }) });
    }

    /// <summary>Reject missing or modified overlays before any lifecycle command can resolve storage.</summary>
    public static void Check(string root, Deployment config)
    {
        if (config.StorageGeneration is null) return;
        var path = OverlayPath(root, config);
        ProtectedFiles.SafePath(path);
        ProtectedFiles.Check(path, 0);
        if (File.ReadAllText(path) != Render(config)) throw new UsageException("Active storage overlay differs from installation authority.");
    }
}
