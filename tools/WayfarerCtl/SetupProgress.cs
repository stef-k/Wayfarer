using System.Security.Cryptography;
using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Small protected setup receipt: exact inputs and three completed application mutations.</summary>
public sealed class SetupProgress
{
    public int Schema { get; init; } = 1;
    public string Fingerprint { get; init; } = "";
    public int Completed { get; set; }
    public bool AdminStarted { get; set; }

    /// <summary>Record ownership before creating any Docker resources; old/unreceipted state is not adopted.</summary>
    public static SetupProgress Create(string root, Deployment config)
    {
        var progress = new SetupProgress { Fingerprint = Inputs(root, config) };
        ProtectedFiles.Create(Path.Combine(root, "setup-progress.json"), JsonSerializer.Serialize(progress));
        return progress;
    }

    /// <summary>Reject replaced credentials, edited bundle/config, invalid progress and unsafe protected files.</summary>
    public static SetupProgress Load(string root, Deployment config)
    {
        var path = Path.Combine(root, "setup-progress.json");
        ProtectedFiles.Check(path, 0);
        var progress = JsonSerializer.Deserialize<SetupProgress>(File.ReadAllText(path), new JsonSerializerOptions
        { UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow });
        if (progress is null || progress.Schema != 1 || progress.Completed is < 0 or > 3 ||
            progress.AdminStarted && progress.Completed < 2 || progress.Completed == 3 && !progress.AdminStarted ||
            progress.Fingerprint != Inputs(root, config))
            throw new UsageException("Setup receipt or original config/bundle/secrets differ; refusing continuation.");
        return progress;
    }

    /// <summary>Flush a new protected receipt before atomic replacement; never truncate the previous checkpoint.</summary>
    public void Save(string root)
    {
        var path = Path.Combine(root, "setup-progress.json");
        ProtectedFiles.Check(path, 0);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        try
        {
            ProtectedFiles.Create(temporary, JsonSerializer.Serialize(this));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    /// <summary>Bind the protected receipt to every consumed bundle input and original secret bytes, never print them.</summary>
    private static string Inputs(string root, Deployment config)
    {
        Deployment.CheckSecrets(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in new[] { Path.Combine(root, "installation.json"), Path.Combine(root, "deployment.env"),
            Path.Combine(root, "secrets/db-password"), Path.Combine(root, "secrets/db-app-password"), Path.Combine(root, "secrets/app-password"),
            Path.Combine(config.Bundle, "compose.yaml"), Path.Combine(config.Bundle, "external.yaml"),
            Path.Combine(config.Bundle, "caddy/Caddyfile"), Path.Combine(config.Bundle, "db/20-wayfarer.sh"),
            Path.Combine(config.Bundle, "config/deployment.env.example") })
        {
            // Hash each file separately to retain unambiguous file boundaries.
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(path)));
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
