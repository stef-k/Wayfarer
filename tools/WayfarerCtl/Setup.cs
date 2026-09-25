using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Fresh-install coordinator; retained partial state always requires deliberate manual recovery.</summary>
public sealed class Setup(IProcessRunner runner, ITerminal terminal)
{
    /// <summary>Parse a bounded setup surface; reject duplicate/unknown options before any host access.</summary>
    public static Dictionary<string, string> Options(string[] args)
    {
        var result = new Dictionary<string, string>();
        var valued = new[] { "--bundle", "--hostname", "--app-digest", "--mode", "--project", "--edge-prefix", "--loopback-port" };
        for (var i = 0; i < args.Length; i++)
        {
            var key = args[i];
            var value = key == "--password-stdin" ? "true" : valued.Contains(key) && i + 1 < args.Length ? args[++i] :
                throw new UsageException("Invalid setup options. Use 'wayfarerctl help setup'.");
            if (!result.TryAdd(key, value)) throw new UsageException("Duplicate setup option.");
        }
        return result;
    }

    /// <summary>Serialize mutations per installation, without treating a stale lock file as durable state.</summary>
    public static FileStream Lock(string root)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var path = Path.Combine(root, "operation.lock");
        if (Path.Exists(path)) ProtectedFiles.Check(path, 0);
        else ProtectedFiles.Create(path, "");
        var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        try { stream.Lock(0, 1); return stream; }
        catch { stream.Dispose(); throw; }
    }

    public async Task<int> RunAsync(string root, string[] args, CancellationToken token)
    {
        var options = Options(args);
        var config = ReadChoices(options);
        config.CheckBundle();
        ProtectedFiles.SafePath(root);
        if (Directory.Exists(root))
        {
            ProtectedFiles.Check(root, 0, directory: true);
            if (Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) is not ("releases" or "operation.lock")))
                throw new UsageException("Existing/partial installation found. Setup never overwrites state; see interrupted-setup recovery.");
        }
        var preflight = new Preflight(runner);
        await preflight.DockerAsync(token);
        await preflight.FreshAsync(config, token);
        await preflight.BundleAsync(root, config, token);
        terminal.Write($"Preflight passed: {root}; project {config.Project}; {config.Mode}; {config.Hostname}; edge {config.EdgePrefix}.0/24.\n" +
            "Fresh DB and app volumes; protected admin bootstrap precedes web/ingress. Existing/native data is never adopted.");
        var password = terminal.Password(options.ContainsKey("--password-stdin"));
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(root, ProtectedFiles.PrivateDirectory);
        using var operationLock = Lock(root);
        // Recheck after taking the lock: another setup may have completed during preflight/password input.
        if (File.Exists(Path.Combine(root, "installation.json")) || Directory.Exists(Path.Combine(root, "secrets")))
            throw new UsageException("Setup state appeared during preflight; refusing overwrite.");
        ProtectedFiles.Create(Path.Combine(root, "installation.json"), JsonSerializer.Serialize(config));
        ProtectedFiles.CreateSecrets(root);
        ProtectedFiles.Create(Path.Combine(root, "deployment.env"), config.EnvironmentFile(root));
        Deployment.CheckSecrets(root);
        try
        {
            await ExecuteAsync(root, config, password, token);
            var result = await new Diagnostics(runner, terminal).RunAsync(root, config, true, token, finishingSetup: true);
            if (result != 0) return result;
            ProtectedFiles.Create(Path.Combine(root, "setup-complete"), "1\n");
            terminal.Write(config.Mode == "managed" ? "Setup complete: protected administrator and public HTTPS readiness verified." :
                "Setup complete: loopback readiness verified. External proxy TLS, forwarding and public reachability remain your responsibility.");
            return 0;
        }
        catch
        {
            terminal.Error("Setup interrupted. Credentials/volumes retained. Do not rerun setup or remove volumes; see recovery guide.");
            throw;
        }
    }

    /// <summary>Exactly the accepted maintenance sequence; stop at the first failure with no rollback/deletion.</summary>
    public async Task ExecuteAsync(string root, Deployment config, string password, CancellationToken token)
    {
        await Step("Compose configuration", config.Compose(root, "config", "--quiet"), null, token);
        await Step("Database healthy", config.Compose(root, "up", "-d", "--wait", "--wait-timeout", "180", "db"), null, token);
        // Fixed container volume roots only; never interpolate an administrator path into this script.
        await Step("Writable volume preparation", config.Compose(root, "run", "--rm", "--no-deps", "-T", "--user", "0", "--entrypoint", "sh", "wayfarer", "-ec",
            "chown 1654:1654 /var/lib/wayfarer /var/cache/wayfarer /var/log/wayfarer; chmod 700 /var/lib/wayfarer; chmod 750 /var/cache/wayfarer /var/log/wayfarer"), null, token);
        await Step("Database migration", config.Compose(root, "run", "--rm", "--no-deps", "-T", "wayfarer", "database", "migrate"), null, token);
        await Step("Database seed", config.Compose(root, "run", "--rm", "--no-deps", "-T", "wayfarer", "database", "seed"), null, token);
        await Step("Protected admin bootstrap", config.Compose(root, "run", "--rm", "--no-deps", "-T", "wayfarer", "admin", "bootstrap", "admin", "--stdin"), password, token);
        await Step("Wayfarer ready", config.Compose(root, "up", "-d", "--wait", "--wait-timeout", "180", "wayfarer"), null, token);
        if (config.Mode == "managed")
            await Step("Managed Caddy", config.Compose(root, "up", "-d", "--wait", "--wait-timeout", "180", "caddy"), null, token);
    }

    private async Task Step(string name, string[] command, string? input, CancellationToken token)
    {
        terminal.Write(name + "...");
        if ((await runner.RunAsync(command, input, token)).Code != 0) throw new IOException("Setup step failed.");
    }

    private Deployment ReadChoices(Dictionary<string, string> options)
    {
        string Choice(string key, string prompt, string? fallback = null)
        {
            if (options.TryGetValue(key, out var value)) return value;
            if (!terminal.Interactive) return fallback ?? throw new UsageException("Missing required setup option. Use 'wayfarerctl help setup'.");
            var answer = terminal.Read(prompt + (fallback is null ? ": " : $" [{fallback}]: ")) ?? throw new OperationCanceledException();
            return answer.Length == 0 ? fallback ?? "" : answer;
        }
        var mode = Choice("--mode", "Proxy mode managed|external", "managed");
        var port = mode == "external" ? Choice("--loopback-port", "Loopback port (proxy hop is edge gateway)", "8080") : options.GetValueOrDefault("--loopback-port", "8080");
        if (!int.TryParse(port, out var number)) throw new UsageException("Invalid loopback port.");
        return new Deployment
        {
            Bundle = Choice("--bundle", "Absolute trusted bundle directory"),
            Hostname = Choice("--hostname", "Public DNS hostname"),
            AppDigest = Choice("--app-digest", "Application sha256 digest from genuine release evidence"),
            Mode = mode, LoopbackPort = number,
            Project = options.GetValueOrDefault("--project", "wayfarer"),
            EdgePrefix = options.GetValueOrDefault("--edge-prefix", "172.30.64")
        };
    }
}
