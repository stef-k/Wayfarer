using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Explicit backup opt-in for completed installations; never rewrites interrupted setup authority.</summary>
public sealed class BackupConfiguration(IProcessRunner runner)
{
    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, uint user, uint group);

    /// <summary>Small command grammar; unspecified policy uses accepted daily defaults.</summary>
    public static Dictionary<string, string> Options(string[] args)
    {
        if (args is ["--disable"] or ["--recover"]) return new() { [args[0]] = "true" };
        var result = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i += 2)
        {
            if (i + 1 >= args.Length || args[i] is not ("--destination" or "--kind" or "--payload" or "--retention" or "--time") ||
                !result.TryAdd(args[i], args[i + 1])) throw new UsageException("Invalid backup configure options.");
        }
        if (!result.ContainsKey("--destination") || !result.ContainsKey("--payload"))
            throw new UsageException("backup configure requires --destination and --payload.");
        BackupPolicy.LiteralPath(result["--destination"]); BackupPolicy.LiteralPath(result["--payload"]);
        return result;
    }

    /// <summary>Stop the owned scheduler, acquire recovery exclusion and commit one complete generation.</summary>
    public async Task<Deployment> ConfigureAsync(string root, Deployment config, string[] args, CancellationToken token)
    {
        if (!File.Exists(Path.Combine(root, "setup-complete"))) throw new UsageException("Backup configuration requires completed setup.");
        var options = Options(args);
        ProvisionControl(root);
        using var exclusion = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
        if (config.Backup is not null)
            await Required(BackupCompose.Command(root, config, "stop", "--timeout", "30", "backup-scheduler"), token);
        if (options.ContainsKey("--recover"))
        {
            BackupGeneration.Recover(root);
            return Deployment.Load(root);
        }
        var installation = config.Installation == Guid.Empty ? Guid.NewGuid() : config.Installation;
        BackupPolicy policy;
        if (options.ContainsKey("--disable")) policy = (config.Backup ?? throw new UsageException("Backup is not configured.")) with { Enabled = false };
        else
        {
            var retention = options.TryGetValue("--retention", out var count) && int.TryParse(count, out var parsed) ? parsed : 7;
            var time = options.GetValueOrDefault("--time", "03:00");
            if (!TimeOnly.TryParseExact(time, "HH:mm", out var daily)) throw new UsageException("Daily time must be HH:mm UTC.");
            var payload = options["--payload"];
            ProtectedFiles.SafePath(payload);
            policy = new BackupPolicy
            {
                Destination = options["--destination"], Kind = options.GetValueOrDefault("--kind", "local"), Payload = payload,
                PayloadSha256 = BackupPolicy.Fingerprint(payload), Retention = retention, DailyMinute = daily.Hour * 60 + daily.Minute
            };
            var source = await InspectAsync(root, config, payload, token);
            var facts = Destination(root, config, policy, installation);
            policy = policy with { DeviceMajor = facts.DeviceMajor, DeviceMinor = facts.DeviceMinor, Inode = facts.Inode,
                Uploads = source.GetProperty("Uploads").GetString()!, Ring = source.GetProperty("Ring").GetString()!,
                Source = new SourceIdentity
                {
                    ApplicationVersion = source.GetProperty("ApplicationVersion").GetString()!,
                    SourceRevision = source.GetProperty("SourceRevision").GetString()!,
                    ExpectedMigrations = source.GetProperty("ExpectedMigrations").Deserialize<string[]>()!,
                    QuartzIdentity = source.GetProperty("QuartzIdentity").GetString()!,
                    ApplicationImage = "ghcr.io/stef-k/wayfarer@" + config.AppDigest,
                    DatabaseImage = "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest,
                    Project = config.Project, BundleFingerprint = BundleFingerprint(config),
                    PayloadFingerprint = policy.PayloadSha256, WorkerVersion = "1"
                } };
        }
        policy = policy with { Generation = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) };
        var next = config with { Schema = 2, Installation = installation, Backup = policy };
        next.Validate(); policy.CheckPayload();
        BackupGeneration.Commit(root, next);
        BackupCompose.Check(root, next);
        await Required(BackupCompose.Command(root, next, "config", "--quiet"), token);
        BackupGeneration.Recover(root);
        return next;
    }

    /// <summary>Read the immutable image's real owners through the additive inspection assembly.</summary>
    private async Task<JsonElement> InspectAsync(string root, Deployment config, string payload, CancellationToken token)
    {
        var inspector = Path.Combine(Path.GetDirectoryName(payload)!, "WayfarerRecoverySource.dll");
        var result = await runner.RunAsync(config.Compose(root, "run", "--rm", "--no-deps", "-T", "--volume",
            inspector + ":/inspection/WayfarerRecoverySource.dll:ro", "--volume", config.Project + "_app-data:/var/lib/wayfarer:ro",
            "--entrypoint", "dotnet", "wayfarer", "exec", "--runtimeconfig", "/app/Wayfarer.runtimeconfig.json",
            "--depsfile", "/app/Wayfarer.deps.json", "/inspection/WayfarerRecoverySource.dll"), null, token);
        if (result.Code != 0 || result.Output.Length > ArchiveContract.ManifestLimit) throw new UsageException("Application recovery source inspection failed.");
        using var document = JsonDocument.Parse(result.Output);
        if (document.RootElement.GetProperty("Schema").GetInt32() != 1 || document.RootElement.GetProperty("ApplicationName").GetString() != "Wayfarer")
            throw new UsageException("Unsupported source inspection contract.");
        return document.RootElement.Clone();
    }

    /// <summary>Prepare only this dedicated empty destination, never recursively change administrator content.</summary>
    private static SafeDirectory.Facts Destination(string root, Deployment config, BackupPolicy policy, Guid installation)
    {
        BackupPolicy.LiteralPath(policy.Destination);
        foreach (var forbidden in new[] { root, config.Bundle, "/var/lib/docker", "/var/lib/wayfarer", "/var/cache/wayfarer", "/var/log/wayfarer", "/etc" })
            if (policy.Destination == forbidden || policy.Destination.StartsWith(forbidden + "/", StringComparison.Ordinal))
                throw new UsageException("Destination overlaps protected host/application state.");
        using var destination = new SafeDirectory(policy.Destination);
        if (policy.Kind == "mounted")
        {
            using var parent = new SafeDirectory(Path.GetDirectoryName(policy.Destination)!);
            if (Path.GetFileName(policy.Destination) != "slot" || !parent.Names().SequenceEqual(new[] { "slot" }) ||
                parent.Identity.Mount == destination.Identity.Mount) throw new UsageException("Mounted destination requires a dedicated parent and mounted slot.");
        }
        else if (policy.Kind != "local") throw new UsageException("Destination kind must be local or mounted.");
        var marker = $"wayfarer-recovery-v1\n{installation:D}\n";
        if (destination.Names().Length == 0)
        {
            if (destination.Identity.User is not (0 or 1654)) throw new UsageException("Destination is not owned by root or UID1654.");
            File.SetUnixFileMode(policy.Destination, ProtectedFiles.PrivateDirectory);
            if (chown(policy.Destination, 1654, 1654) != 0) throw new IOException("Destination ownership failed.");
            ProtectedFiles.Create(Path.Combine(policy.Destination, ".wayfarer-recovery"), marker, 1654);
        }
        using var existing = destination.Read(".wayfarer-recovery");
        if (existing.Length > 128 || new StreamReader(existing).ReadToEnd() != marker) throw new UsageException("Destination belongs to another installation.");
        ProtectedFiles.Check(policy.Destination, 1654, directory: true);
        return destination.Identity;
    }

    /// <summary>Provision the stable local lock once; existing ownership must match exactly.</summary>
    private static void ProvisionControl(string root)
    {
        var directory = Path.Combine(root, "recovery-control");
        if (!Directory.Exists(directory))
        {
            Directory.CreateDirectory(directory, ProtectedFiles.PrivateDirectory);
            if (chown(directory, 1654, 1654) != 0) throw new IOException("Cannot provision recovery control.");
            ProtectedFiles.Create(Path.Combine(directory, "recovery.lock"), "", 1654);
        }
        ProtectedFiles.Check(directory, 1654, directory: true);
        ProtectedFiles.Check(Path.Combine(directory, "recovery.lock"), 1654);
    }

    private static string BundleFingerprint(Deployment config)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "compose.yaml", "external.yaml", "caddy/Caddyfile", "db/20-wayfarer.sh" })
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(Path.Combine(config.Bundle, name))));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private async Task Required(string[] arguments, CancellationToken token)
    {
        if ((await runner.RunAsync(arguments, null, token)).Code != 0) throw new IOException("Backup Compose operation failed.");
    }
}
