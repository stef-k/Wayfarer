using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Literal additive Compose material; existing services, networks and volumes are never overridden.</summary>
public static class BackupCompose
{
    /// <summary>Generation-specific immutable filenames allow policy replacement without mixed worker input.</summary>
    public static string DirectoryPath(string root, BackupPolicy policy) => Path.Combine(root, "recovery-generations", policy.Generation);

    /// <summary>Use the original project/bundle plus exactly the generated maintenance overlay.</summary>
    public static string[] Command(string root, Deployment config, params string[] arguments)
    {
        var policy = config.Backup ?? throw new UsageException("Backup is not configured.");
        Check(root, config);
        return config.Compose(root, ["-f", Path.Combine(DirectoryPath(root, policy), "compose.json"), "--profile", "backup", .. arguments]);
    }

    /// <summary>One security envelope for manual and socket-free scheduled execution in the exact DB image.</summary>
    public static string Render(string root, Deployment config)
    {
        var policy = config.Backup ?? throw new UsageException("Backup is not configured.");
        var generation = DirectoryPath(root, policy);
        object Bind(string source, string target, bool readOnly, string propagation = "rprivate") => new
        { type = "bind", source, target, read_only = readOnly, bind = new { create_host_path = false, propagation } };
        var mounts = new object[]
        {
            Bind(policy.Payload, "/worker/wayfarer-recovery", true),
            Bind(Path.Combine(generation, "worker.json"), "/config/worker.json", true),
            Bind(Path.Combine(root, "recovery-control"), "/control", false),
            new { type = "volume", source = "app-data", target = "/source", read_only = true, volume = new { nocopy = true } },
            policy.Kind == "local" ? Bind(policy.Destination, "/destination/slot", false) :
                Bind(Path.GetDirectoryName(policy.Destination)!, "/destination", false, "rslave")
        };
        object Service(bool scheduler) => new
        {
            image = "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest, platform = "linux/amd64",
            profiles = new[] { "backup" }, user = "1654:1654", read_only = true, init = true,
            cap_drop = new[] { "ALL" }, security_opt = new[] { "no-new-privileges:true" },
            entrypoint = new[] { "/worker/wayfarer-recovery" }, command = new[] { scheduler ? "schedule" : "backup" },
            restart = scheduler ? "unless-stopped" : "no", stop_grace_period = "30s",
            cpus = 1, mem_limit = "512m", pids_limit = 64,
            tmpfs = new[] { "/tmp:uid=1654,gid=1654,mode=0700,size=2147483648" },
            networks = new[] { "backend" }, secrets = new[] { "app-password" }, volumes = mounts
        };
        return JsonSerializer.Serialize(new { services = new Dictionary<string, object>
        { ["backup-worker"] = Service(false), ["backup-scheduler"] = Service(true) } });
    }

    /// <summary>Check every derived byte against installation authority before executing a maintenance service.</summary>
    public static void Check(string root, Deployment config)
    {
        var policy = config.Backup ?? throw new UsageException("Backup is not configured.");
        policy.CheckPayload();
        if (policy.Source.BundleFingerprint != BackupConfiguration.BundleFingerprint(config) ||
            policy.Source.ApplicationImage != "ghcr.io/stef-k/wayfarer@" + config.AppDigest ||
            policy.Source.DatabaseImage != "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest || policy.Source.Project != config.Project)
            throw new UsageException("Recovery source/bundle identity changed; no application update is allowed.");
        var directory = DirectoryPath(root, policy);
        foreach (var (name, expected) in new[]
        {
            ("compose.json", Render(root, config)),
            ("worker.json", JsonSerializer.Serialize(policy.Worker(config.Installation), WayfarerRecovery.ArchiveContract.Json))
        })
        {
            var path = Path.Combine(directory, name);
            ProtectedFiles.SafePath(name == "worker.json" ? directory : path);
            if (File.ReadAllText(path) != expected) throw new UsageException("Generated recovery input differs from installation authority.");
        }
    }
}
