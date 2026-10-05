using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Reuse real protected release/secret fixtures and the existing process seam for receipt-owned lifecycle cases.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
internal sealed class UninstallCommandFixture : IDisposable
{
    private readonly ReleaseBundleTests releases = new();
    private HttpListener? endpoint;
    private readonly string? executable;
    /// <summary>Docker/backup fixture storage lives outside the installation cleanup authority, as on a supported host.</summary>
    private readonly string external = Path.Combine(Path.GetTempPath(), "wayfarer-uninstall-storage-" + Guid.NewGuid().ToString("N"));

    /// <summary>One owned temporary installation has canonical runtime and all active volumes; no Docker daemon is called.</summary>
    internal UninstallCommandFixture(bool? backupEnabled = null, bool managed = false, bool hostedOperator = false)
    {
        ProtectedFiles.RequireRoot();
        Directory.CreateDirectory(external, ProtectedFiles.PrivateDirectory);
        Root = Path.Combine(Path.GetTempPath(), "wayfarer-uninstall-command-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root, ProtectedFiles.PrivateDirectory);
        var bundle = releases.RetainOperator(Root, "1.9.22", !hostedOperator, true);
        executable = hostedOperator ? Path.Combine(bundle.Directory, "wayfarerctl") : null;
        using var port = new TcpListener(IPAddress.Loopback, 0);
        port.Start();
        Config = UninstallPlanningTests.Config() with { Bundle = bundle.Directory, Release = ReleaseAuthority.From(bundle),
            AppDigest = bundle.Manifest.Images.PlatformDigest, DbDigest = bundle.Manifest.Images.DatabaseDigest,
            Platform = NativePlatform.Current, Mode = managed ? "managed" : "external", LoopbackPort = ((IPEndPoint)port.LocalEndpoint).Port };
        ProtectedFiles.Create(Path.Combine(Root, "setup-complete"), "1\n");
        ProtectedFiles.CreateSecrets(Root);
        if (backupEnabled is { } enabled)
        {
            var destination = Path.Combine(external, "destination");
            Directory.CreateDirectory(destination, ProtectedFiles.PrivateDirectory);
            if (chown(destination, 1654, 1654) != 0) throw new IOException("Fixture destination ownership failed.");
            using var directory = new SafeDirectory(destination);
            var facts = directory.Identity;
            var installation = Guid.NewGuid();
            ProtectedFiles.Create(Path.Combine(destination, ".wayfarer-recovery"), $"wayfarer-recovery-v1\n{installation:D}\n", 1654);
            Config = Config with { Installation = installation, Backup = new BackupPolicy { Enabled = enabled,
                Destination = destination, DeviceMajor = facts.DeviceMajor, DeviceMinor = facts.DeviceMinor, Inode = facts.Inode,
                Payload = Path.Combine(bundle.Directory, "wayfarer-recovery"), PayloadSha256 = bundle.Target(Config.Project).PayloadFingerprint,
                Generation = new string('a', 64), Source = bundle.Target(Config.Project) with
                { BundleFingerprint = BackupConfiguration.BundleFingerprint(Config) }, Uploads = "uploads", Ring = "data-protection" } };
            BackupConfiguration.ProvisionControl(Root, true);
            var generation = BackupCompose.DirectoryPath(Root, Config.Backup);
            Directory.CreateDirectory(generation, ProtectedFiles.PrivateDirectory);
            ProtectedFiles.Create(Path.Combine(generation, "compose.json"), BackupCompose.Render(Root, Config));
            ProtectedFiles.Create(Path.Combine(generation, "worker.json"), JsonSerializer.Serialize(Config.Backup.Worker(installation), ArchiveContract.Json), 1654);
        }
        ProtectedFiles.Create(Path.Combine(Root, "installation.json"), JsonSerializer.Serialize(Config));
        ProtectedFiles.Create(Path.Combine(Root, "deployment.env"), Config.EnvironmentFile(Root));
        Runner = new DockerRunner(Root, Config, Path.Combine(external, "docker"));
        foreach (var role in UninstallInventory.Roles(Config))
            Directory.CreateDirectory(Path.Combine(Runner.DockerRoot, "volumes", ActiveStorage.Volume(Config, role), "_data"));
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, uint user, uint group);

    internal string Root { get; }
    internal Deployment Config { get; }
    internal DockerRunner Runner { get; }
    internal RecordingTerminal Terminal { get; } = new();
    /// <summary>Propagate the existing protected-publication observation seam for command-level purge interruption evidence.</summary>
    internal Action<string>? PurgeCheckpoint { get; set; }

    /// <summary>Exercise the public CLI with its actual retained native/hosted executable authority.</summary>
    internal Task<int> Command(params string[] args) => new Cli(Runner, Terminal) { ExecutablePath = executable, PurgeCheckpoint = PurgeCheckpoint }
        .RunAsync(["--deployment-root", Root, .. args]);

    /// <summary>The planning command publishes one protected plan; tests consume its file rather than inventing authorization.</summary>
    internal async Task<UninstallPlan> Plan(bool backup = false, bool purge = false)
    {
        Assert.Equal(0, await Command(["uninstall", .. purge ? new[] { "--purge" } : [], "--plan", backup ? "--backup" : "--without-backup"]));
        return ReadPlan();
    }

    /// <summary>Use the last structured plan printed by the command to find its exact protected bytes.</summary>
    internal UninstallPlan ReadPlan() => JsonSerializer.Deserialize<UninstallPlan>(Terminal.Output.Last(line => line.StartsWith('{')))!;

    /// <summary>Serve the ordinary read-only HTTP health observations, leaving all product diagnostics enabled.</summary>
    internal void EnableEndpoint()
    {
        endpoint = new HttpListener();
        // Diagnostics deliberately supplies the configured public Host header on its loopback request.
        endpoint.Prefixes.Add($"http://*:{Config.LoopbackPort}/");
        endpoint.Start();
        _ = Task.Run(async () =>
        {
            try
            {
                while (endpoint.IsListening)
                {
                    var request = await endpoint.GetContextAsync();
                    var body = request.Request.Url!.AbsolutePath == "/health/live" ? "live" : "ready";
                    var bytes = Encoding.UTF8.GetBytes(body);
                    request.Response.ContentLength64 = bytes.Length;
                    await request.Response.OutputStream.WriteAsync(bytes);
                    request.Response.Close();
                }
            }
            catch (Exception error) when (error is HttpListenerException or ObjectDisposedException) { }
        });
    }

    /// <summary>Collect safe output and bounded supplied answers, without hidden interactive defaults.</summary>
    internal sealed class RecordingTerminal : ITerminal
    {
        public bool Interactive { get; set; }
        internal Queue<string?> Answers { get; } = new();
        internal List<string> Output { get; } = [];
        internal List<string> Errors { get; } = [];
        internal List<string> Prompts { get; } = [];
        public void Write(string message) => Output.Add(message);
        public void Error(string message) => Errors.Add(message);
        public string? Read(string prompt) { Prompts.Add(prompt); return Answers.Dequeue(); }
        public string Password(bool fromStdin) => throw new InvalidOperationException("Unexpected password prompt.");
    }

    /// <summary>Stateful exact Docker observations model only shipped container/network lifecycle and existing recovery worker output.</summary>
    internal sealed class DockerRunner : IProcessRunner
    {
        private readonly string root;
        private readonly Deployment config;
        private readonly Dictionary<string, string> workers = [];
        private int incarnation;

        internal DockerRunner(string root, Deployment config, string dockerRoot)
        {
            this.root = root;
            this.config = config;
            DockerRoot = dockerRoot;
            Recreate();
        }

        internal string DockerRoot { get; }
        internal List<string[]> Calls { get; } = [];
        internal Dictionary<string, JsonElement> Containers { get; } = [];
        internal Dictionary<string, JsonElement> Networks { get; } = [];
        internal Dictionary<string, JsonElement> AdditionalVolumes { get; } = [];
        internal HashSet<string> MissingVolumes { get; } = [];
        internal Action<string[]>? Before { get; set; }
        internal string? Failure { get; set; }
        internal string? LostRemoval { get; set; }
        internal bool InvalidIntegrity { get; set; }
        internal bool InvalidCompatibility { get; set; }
        internal bool WrongArchive { get; set; }
        internal bool WrongName { get; set; }
        internal bool ExtraArchive { get; set; }
        internal bool TruncatedInventory { get; set; }
        /// <summary>The real worker reports a committed capture but exits nonzero when older-set retention fails.</summary>
        internal bool RetentionSucceeded { get; set; } = true;
        internal BackupResult? Captured { get; private set; }

        /// <summary>Refuse every unmodeled command, including image deletion; volume removals use only exact accepted names.</summary>
        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
        {
            Calls.Add(args);
            Before?.Invoke(args);
            if (Failure == args[0] || Failure == "up" && args.Contains("up")) return Task.FromResult(new ProcessResult(1, "sensitive child diagnostics"));
            var output = "";
            var code = 0;
            if (args is ["info", "--format", "{{.DockerRootDir}}"]) output = DockerRoot;
            else if (args[0] == "info") output = NativePlatform.Current;
            else if (args is ["compose", "version", "--short"]) output = "2.24.4";
            else if (args[0] == "compose") return Task.FromResult(Compose(args));
            else if (args[0] == "ps") output = TruncatedInventory ? new string('x', 262144) : ListContainers(args);
            else if (args is ["container", "inspect", _] or ["inspect", _])
            {
                var identity = args[^1];
                var match = Containers.Values.SingleOrDefault(c => c.GetProperty("Id").GetString() == identity || c.GetProperty("Name").GetString()!.TrimStart('/') == identity);
                if (match.ValueKind == JsonValueKind.Undefined) code = 1;
                else output = JsonSerializer.Serialize(new[] { match });
            }
            else if (args is ["volume", "ls", ..]) output = string.Join('\n', UninstallInventory.Roles(config)
                .Select(role => UninstallPlanningTests.Volume(config, role)).Concat(AdditionalVolumes.Values)
                .Where(volume => MatchesLabel(args, volume)).Select(volume => volume.GetProperty("Name").GetString()!)
                .Where(name => !MissingVolumes.Contains(name)));
            else if (args is ["volume", "inspect", var volumeName])
            {
                var volume = AdditionalVolumes.TryGetValue(volumeName, out var additional) ? additional :
                    UninstallPlanningTests.Volume(config, UninstallInventory.Roles(config).Single(role => ActiveStorage.Volume(config, role) == volumeName));
                var facts = volume.Deserialize<Dictionary<string, JsonElement>>()!;
                facts["Mountpoint"] = JsonSerializer.SerializeToElement(Path.Combine(DockerRoot, "volumes", volumeName, "_data"));
                output = JsonSerializer.Serialize(new[] { facts });
            }
            else if (args is ["volume", "rm", var removedVolume])
            {
                MissingVolumes.Add(removedVolume);
                Directory.Delete(Path.Combine(DockerRoot, "volumes", removedVolume, "_data"));
                code = LostRemoval == "volume" ? 1 : 0;
            }
            else if (args is ["network", "ls", ..]) output = string.Join('\n', args.Contains("-q")
                ? Networks.Values.Where(n => MatchesLabel(args, n)).Select(n => n.GetProperty("Id").GetString())
                : Networks.Values.Where(n => MatchesLabel(args, n)).Select(n => n.GetProperty("Name").GetString()));
            else if (args is ["network", "inspect", var networkName]) output = JsonSerializer.Serialize(new[]
                { Networks.Values.Single(n => n.GetProperty("Name").GetString() == networkName || n.GetProperty("Id").GetString() == networkName) });
            else if (args is ["update", "--restart=no", var updateId]) Change(updateId, false);
            else if (args is ["stop", "--time", _, var stopId]) Change(stopId, true);
            else if (args[0] == "wait") output = workers.TryGetValue(args[^1], out var operation) &&
                (Failure == operation || operation == "backup" && !RetentionSucceeded) ? "1" : "0";
            else if (args is ["rm", var containerId])
            {
                if (!workers.Remove(containerId)) Containers.Remove(Containers.Single(pair => pair.Value.GetProperty("Id").GetString() == containerId).Key);
                code = LostRemoval == "container" ? 1 : 0;
            }
            else if (args is ["network", "rm", var networkId])
            {
                Networks.Remove(Networks.Single(pair => pair.Value.GetProperty("Id").GetString() == networkId).Key);
                code = LostRemoval == "network" ? 1 : 0;
            }
            else if (args[0] == "logs") output = WorkerResult(args[^1]);
            else if (args is ["image", "inspect", ..]) output = "[{\"Config\":{\"Labels\":{\"org.opencontainers.image.version\":\"1.9.22\"}}}]";
            else throw new InvalidOperationException("Unexpected Docker call: " + string.Join(' ', args));
            return Task.FromResult(new ProcessResult(code, output));
        }

        /// <summary>Daemon label queries do not return unrelated sentinels merely because the fake stores them in the same dictionary.</summary>
        private static bool MatchesLabel(string[] args, JsonElement resource)
        {
            if (!args.Contains("--filter")) return true;
            var parts = args[Array.IndexOf(args, "--filter") + 1][6..].Split('=', 2);
            return resource.GetProperty("Labels").TryGetProperty(parts[0], out var value) && value.GetString() == parts[1];
        }

        /// <summary>Consumer filters return exact mounted/networked IDs, including a newly introduced foreign consumer.</summary>
        private string ListContainers(string[] args)
        {
            var selected = Containers.Values.AsEnumerable();
            var filter = args.Contains("--filter") ? args[Array.IndexOf(args, "--filter") + 1] : "";
            if (filter.StartsWith("label="))
            {
                var label = filter[6..].Split('=', 2);
                selected = selected.Where(c => c.GetProperty("Config").GetProperty("Labels").TryGetProperty(label[0], out var value) && value.GetString() == label[1]);
            }
            if (filter.StartsWith("volume=")) selected = selected.Where(c => c.GetProperty("Mounts").EnumerateArray()
                .Any(m => m.GetProperty("Type").GetString() == "volume" && m.GetProperty("Name").GetString() == filter[7..]));
            if (filter.StartsWith("network=")) selected = selected.Where(c => c.GetProperty("NetworkSettings").GetProperty("Networks").TryGetProperty(filter[8..], out _));
            if (args.Contains("-q")) selected = selected.Where(c => c.GetProperty("State").GetProperty("Running").GetBoolean());
            return string.Join('\n', selected.Select(c => args.Contains("{{.Names}}") ? c.GetProperty("Name").GetString()!.TrimStart('/') : c.GetProperty("Id").GetString()));
        }

        /// <summary>Model retained Compose start/stop and the existing detached recovery service invocation.</summary>
        private ProcessResult Compose(string[] args)
        {
            if (args.Contains("stop"))
            {
                foreach (var pair in Containers.ToArray())
                    if (args[^1] == pair.Key || args[^1] == "70") Change(pair.Value.GetProperty("Id").GetString()!, true, false);
            }
            else if (args.Contains("run")) workers.Add(args[Array.IndexOf(args, "--name") + 1], args.Contains("backup-worker") ? "backup" : "verify");
            else if (args.Contains("up")) Recreate(args.Contains("backup-scheduler"));
            else if (args.Contains("ps")) return new(0, Containers.TryGetValue(args[^1], out var container) ? container.GetProperty("Id").GetString()! : "");
            else if (args.Contains("exec")) return new(0, args.Contains("psql") ? "18.6 (Debian 18.6-1.pgdg12+2)|3.6.4|1.8" : args[^1] == "version" ? "Wayfarer 1.9.22" : "");
            else throw new InvalidOperationException("Unexpected Compose call.");
            return new(0, "");
        }

        /// <summary>Existing backup publication is simulated only at the process result seam; exact pair bytes are real filesystem evidence.</summary>
        private string WorkerResult(string name)
        {
            if (workers[name] == "backup")
            {
                var completed = DateTimeOffset.UtcNow;
                var archive = Guid.NewGuid();
                var basename = ArchiveContract.Name(config.Installation, completed, archive);
                Captured = new(1, archive, basename, completed, RetentionSucceeded);
                var bytes = Encoding.UTF8.GetBytes("exact newly captured archive bytes");
                File.WriteAllBytes(Path.Combine(config.Backup!.Destination, basename), bytes);
                File.WriteAllText(Path.Combine(config.Backup.Destination, basename + ".sha256"), $"{Convert.ToHexStringLower(SHA256.HashData(bytes))}  {basename}\n");
                if (ExtraArchive) File.WriteAllBytes(Path.Combine(config.Backup.Destination, ArchiveContract.Name(config.Installation, completed.AddMinutes(1), archive)), bytes);
                return JsonSerializer.Serialize(Captured);
            }
            return JsonSerializer.Serialize(new VerifyResult(1, !InvalidIntegrity, !InvalidCompatibility,
                WrongArchive ? Guid.NewGuid() : Captured!.Archive, WrongName ? "wrong.tar" : Captured!.Name, "quiesced"));
        }

        /// <summary>Each canonical incarnation has new runtime IDs while the same real volume directories remain retained.</summary>
        private void Recreate(bool schedulerOnly = false)
        {
            incarnation++;
            foreach (var service in UninstallInventory.Services(config).Where(service =>
                schedulerOnly ? service == "backup-scheduler" : service != "backup-scheduler" || config.Backup!.Enabled))
                if (!Containers.ContainsKey(service)) Containers.Add(service, Container(service));
            foreach (var name in new[] { "backend", "edge" })
                if (!Networks.ContainsKey(config.Project + "_" + name)) Networks.Add(config.Project + "_" + name, JsonSerializer.SerializeToElement(new
                { Id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(name + incarnation))), Name = config.Project + "_" + name,
                    Created = "2026-10-05", Driver = "bridge", Scope = "local", Internal = name == "backend", Options = new { },
                    IPAM = new { Config = Array.Empty<object>() }, Labels = new Dictionary<string, string>
                    { ["com.docker.compose.project"] = config.Project, ["com.docker.compose.network"] = name } }));
            foreach (var pair in Containers.ToArray()) Change(pair.Value.GetProperty("Id").GetString()!, false, false, true);
        }

        /// <summary>Canonical facts mirror the concrete production ownership checks, including secrets, proxy mounts and recovery hardening.</summary>
        private JsonElement Container(string service)
        {
            var labels = new Dictionary<string, string> { ["com.docker.compose.project"] = config.Project, ["com.docker.compose.service"] = service,
                ["com.docker.compose.project.working_dir"] = config.Bundle, ["com.docker.compose.oneoff"] = "False",
                ["com.docker.compose.project.config_files"] = config.Bundle + "/compose.yaml" + (config.Mode == "external" ? "," + config.Bundle + "/external.yaml" : "") +
                    (service == "backup-scheduler" ? "," + BackupCompose.DirectoryPath(root, config.Backup!) + "/compose.json" : "") };
            var mounts = new List<object>();
            void Volume(string role, string target, bool readOnly = false) => mounts.Add(new { Type = "volume", Name = ActiveStorage.Volume(config, role), Source = "/docker/" + role, Destination = target, RW = !readOnly });
            void Bind(string source, string target, bool readOnly = true) => mounts.Add(new { Type = "bind", Source = source, Destination = target, RW = !readOnly });
            if (service == "db") { Volume("db-data", "/var/lib/postgresql"); Bind(root + "/secrets/db-password", "/run/secrets/db-password");
                Bind(root + "/secrets/db-app-password", "/run/secrets/app-password"); Bind(config.Bundle + "/db/20-wayfarer.sh", "/docker-entrypoint-initdb.d/20-wayfarer.sh"); }
            if (service == "wayfarer") { Volume("app-data", "/var/lib/wayfarer"); Volume("app-cache", "/var/cache/wayfarer");
                Volume("app-logs", "/var/log/wayfarer"); Bind(root + "/secrets/app-password", "/run/secrets/app-password"); }
            if (service == "caddy") { Volume("caddy-data", "/data"); Volume("caddy-config", "/config"); Bind(config.Bundle + "/caddy/Caddyfile", "/etc/caddy/Caddyfile"); }
            if (service == "backup-scheduler")
            {
                var policy = config.Backup!;
                Volume("app-data", "/source", true); Bind(policy.Payload, "/worker/wayfarer-recovery");
                Bind(BackupCompose.DirectoryPath(root, policy) + "/worker.json", "/config/worker.json"); Bind(policy.Destination, "/destination/slot", false);
                Bind(root + "/secrets/app-password", "/run/secrets/app-password"); Bind(root + "/recovery-control", "/control");
                Bind(root + "/recovery-control/recovery.lock", "/control/recovery.lock", false); Bind(root + "/recovery-control/state", "/control/state", false);
            }
            var networks = service is "db" or "backup-scheduler" ? new[] { "backend" } : service == "caddy" ? new[] { "edge" } : new[] { "backend", "edge" };
            return JsonSerializer.SerializeToElement(new { Id = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(service + incarnation))),
                Name = "/" + config.Project + "-" + service + "-1", Created = "2026-10-05", Image = new string('e', 64),
                Config = new { Image = service == "caddy" ? "caddy@" + ReleaseContract.CaddyDigest : "ghcr.io/stef-k/" + (service == "wayfarer" ? "wayfarer@" + config.AppDigest : "wayfarer-db@" + config.DbDigest),
                    Labels = labels, Cmd = new[] { service == "backup-scheduler" ? "schedule" : "run" }, User = "1654:1654",
                    Env = new[] { "Storage__DataRoot=/var/lib/wayfarer", "Storage__CacheRoot=/var/cache/wayfarer", "Storage__LogRoot=/var/log/wayfarer",
                        "Storage__TempRoot=/tmp/wayfarer", "DataProtection__KeyRingPath=/var/lib/wayfarer/data-protection" } }, Mounts = mounts,
                HostConfig = new { RestartPolicy = new { Name = "unless-stopped" }, ReadonlyRootfs = true, Privileged = false },
                State = new { Running = true, Status = "running", Health = new { Status = "healthy" } },
                NetworkSettings = new { Networks = networks.ToDictionary(n => config.Project + "_" + n, _ => new { }),
                    Ports = service == "wayfarer" && config.Mode == "external" ? new Dictionary<string, object>
                        { ["8080/tcp"] = new[] { new { HostIp = "127.0.0.1", HostPort = config.LoopbackPort.ToString() } } } : [] } });
        }

        /// <summary>Restart and running facts vary independently from the stable evidence bound by Handoff 1.</summary>
        private void Change(string id, bool stop, bool disableRestart = true, bool running = false)
        {
            var pair = Containers.Single(c => c.Value.GetProperty("Id").GetString() == id);
            var facts = pair.Value.Deserialize<Dictionary<string, JsonElement>>()!;
            if (disableRestart) facts["HostConfig"] = JsonSerializer.SerializeToElement(new { RestartPolicy = new { Name = "no" }, ReadonlyRootfs = true, Privileged = false });
            if (stop || running) facts["State"] = JsonSerializer.SerializeToElement(new { Running = running, Status = running ? "running" : "exited", Health = new { Status = "healthy" } });
            Containers[pair.Key] = JsonSerializer.SerializeToElement(facts);
        }
    }

    /// <summary>Reclaim only the fixture-owned directory and retained release fixture; no production installation is touched.</summary>
    public void Dispose()
    {
        endpoint?.Close();
        Directory.Delete(Root, true);
        Directory.Delete(external, true);
        releases.Dispose();
    }
}
