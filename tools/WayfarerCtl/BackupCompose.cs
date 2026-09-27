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

    /// <summary>Select the least authority needed by the fixed private worker operation.</summary>
    public static string ServiceFor(string operation) => operation switch
    {
        "backup" => "backup-worker",
        "backups" or "verify" => "backup-reader",
        "destination-check" => "backup-destination-check",
        _ => throw new UsageException("Unsupported recovery operation.")
    };

    /// <summary>Capture alone receives source/DB authority; offline parsing has a read-only destination and no network.</summary>
    public static string Render(string root, Deployment config)
    {
        var policy = config.Backup ?? throw new UsageException("Backup is not configured.");
        var generation = DirectoryPath(root, policy);
        var control = Path.Combine(root, "recovery-control");
        object Bind(string source, string target, bool readOnly, string propagation = "rprivate") => new
        { type = "bind", source, target, read_only = readOnly, bind = new { create_host_path = false, propagation } };
        object Service(string operation)
        {
            var scheduler = operation == "schedule";
            var capture = scheduler || operation == "backup";
            var probe = operation == "destination-check";
            var mounts = new List<object>
            {
                Bind(policy.Payload, "/worker/wayfarer-recovery", true),
                Bind(Path.Combine(generation, "worker.json"), "/config/worker.json", true),
                policy.Kind == "local" ? Bind(policy.Destination, "/destination/slot", !capture && !probe) :
                    Bind(Path.GetDirectoryName(policy.Destination)!, "/destination", !capture && !probe, "rslave")
            };
            if (!probe)
            {
                mounts.Add(Bind(control, "/control", true));
                mounts.Add(Bind(Path.Combine(control, "recovery.lock"), "/control/recovery.lock", false));
            }
            if (scheduler) mounts.Add(Bind(Path.Combine(control, "state"), "/control/state", false));
            if (capture) mounts.Add(new { type = "volume", source = "app-data", target = "/source", read_only = true, volume = new { nocopy = true } });
            var service = new Dictionary<string, object>
            {
                ["image"] = "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest, ["platform"] = "linux/amd64",
                ["profiles"] = new[] { "backup" }, ["user"] = "1654:1654", ["read_only"] = true, ["init"] = true,
                ["cap_drop"] = new[] { "ALL" }, ["security_opt"] = new[] { "no-new-privileges:true" },
                ["entrypoint"] = new[] { "/worker/wayfarer-recovery" }, ["command"] = new[] { operation },
                ["restart"] = scheduler ? "unless-stopped" : "no", ["stop_grace_period"] = "30s",
                ["cpus"] = 1, ["mem_limit"] = "512m", ["pids_limit"] = 64,
                ["tmpfs"] = new[] { "/tmp:uid=1654,gid=1654,mode=0700,size=2147483648" }, ["volumes"] = mounts
            };
            if (capture)
            {
                service["networks"] = new[] { "backend" };
                service["secrets"] = new[] { "app-password" };
            }
            else service["network_mode"] = "none";
            return service;
        }
        return JsonSerializer.Serialize(new { services = new Dictionary<string, object>
        {
            ["backup-worker"] = Service("backup"), ["backup-scheduler"] = Service("schedule"),
            ["backup-reader"] = Service("backups"), ["backup-destination-check"] = Service("destination-check")
        } });
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
