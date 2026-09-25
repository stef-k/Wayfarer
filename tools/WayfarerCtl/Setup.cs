using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Fresh-install coordinator with explicit continuation of verified, owned partial state.</summary>
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
            var value = key is "--password-stdin" or "--resume" or "--retry-admin" ? "true" : valued.Contains(key) && i + 1 < args.Length ? args[++i] :
                throw new UsageException("Invalid setup options. Use 'wayfarerctl help setup'.");
            if (!result.TryAdd(key, value)) throw new UsageException("Duplicate setup option.");
        }
        if (result.ContainsKey("--retry-admin") && !result.ContainsKey("--resume"))
            throw new UsageException("--retry-admin requires --resume.");
        if (result.ContainsKey("--resume") && result.Keys.Any(key => key is not ("--resume" or "--retry-admin" or "--password-stdin")))
            throw new UsageException("Resume uses the original protected configuration; setup choices cannot change.");
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
        if (options.ContainsKey("--resume")) return await ResumeAsync(root, options, token);
        var config = ReadChoices(options);
        config.CheckBundle();
        ProtectedFiles.SafePath(root);
        if (Directory.Exists(root))
        {
            ProtectedFiles.Check(root, 0, directory: true);
            if (Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) is not ("releases" or "operation.lock")))
                throw new UsageException("Existing/partial installation found. Use setup --resume for verified wayfarerctl state; never overwrite it.");
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
        var progress = SetupProgress.Create(root, config);
        return await FinishAsync(root, config, progress, password, false, token);
    }

    /// <summary>Continue only the protected original identity, under the same installation lock.</summary>
    private async Task<int> ResumeAsync(string root, Dictionary<string, string> options, CancellationToken token)
    {
        ProtectedFiles.SafePath(root);
        ProtectedFiles.Check(root, 0, directory: true);
        using var operationLock = Lock(root);
        var config = Deployment.Load(root);
        if (File.Exists(Path.Combine(root, "setup-complete")))
            throw new UsageException("Setup is already complete; use status/doctor or lifecycle commands.");
        var progress = SetupProgress.Load(root, config);
        var preflight = new Preflight(runner);
        await preflight.DockerAsync(token);
        await preflight.BundleAsync(root, config, token);
        await preflight.ResumeAsync(root, config, token, progress.Completed > 0);
        terminal.Write("Resuming verified installation; existing credentials and durable volumes are retained.");
        // Do not request or retain a password when bootstrap is already complete or needs observation first.
        var password = progress.Completed < 3 && (!progress.AdminStarted || options.ContainsKey("--retry-admin"))
            ? terminal.Password(options.ContainsKey("--password-stdin")) : "";
        return await FinishAsync(root, config, progress, password, options.ContainsKey("--retry-admin"), token);
    }

    /// <summary>Completion requires live diagnostics; all failures retain the last durable checkpoint.</summary>
    private async Task<int> FinishAsync(string root, Deployment config, SetupProgress progress, string password, bool retryAdmin, CancellationToken token)
    {
        try
        {
            await ExecuteAsync(root, config, password, token, progress, () => progress.Save(root), retryAdmin);
            var result = await new Diagnostics(runner, terminal).RunAsync(root, config, true, token, finishingSetup: true);
            if (result != 0)
            {
                terminal.Error("Setup verification incomplete. Correct the reported cause, then run setup --resume.");
                return result;
            }
            ProtectedFiles.Create(Path.Combine(root, "setup-complete"), "1\n");
            terminal.Write(config.Mode == "managed" ? "Setup complete: protected administrator and public HTTPS readiness verified." :
                "Setup complete: loopback readiness verified. External proxy TLS, forwarding and public reachability remain your responsibility.");
            return 0;
        }
        catch
        {
            terminal.Error("Setup interrupted. Credentials/volumes retained. Correct the cause, then run setup --resume; see recovery guide.");
            throw;
        }
    }

    /// <summary>Skip committed maintenance; retry safe convergence steps and observe uncertain admin creation.</summary>
    public async Task ExecuteAsync(string root, Deployment config, string password, CancellationToken token,
        SetupProgress? progress = null, Action? checkpoint = null, bool retryAdmin = false)
    {
        progress ??= new SetupProgress();
        await Step("Compose configuration", config.Compose(root, "config", "--quiet"), null, token);
        await Step("Database healthy", config.Compose(root, "up", "-d", "--wait", "--wait-timeout", "180", "db"), null, token);
        // Fixed container volume roots only; never interpolate an administrator path into this script.
        await Step("Writable volume preparation", config.Compose(root, "run", "--rm", "--no-deps", "-T", "--user", "0", "--entrypoint", "sh", "wayfarer", "-ec",
            "chown 1654:1654 /var/lib/wayfarer /var/cache/wayfarer /var/log/wayfarer; chmod 700 /var/lib/wayfarer; chmod 750 /var/cache/wayfarer /var/log/wayfarer"), null, token);
        foreach (var (number, operation) in new[] { (1, "migrate"), (2, "seed") })
        {
            if (progress.Completed >= number) continue;
            await Step("Database " + operation, config.Compose(root, "run", "--rm", "--no-deps", "-T", "wayfarer", "database", operation), null, token);
            progress.Completed = number;
            checkpoint?.Invoke();
        }
        if (progress.Completed < 3)
            await BootstrapAsync(root, config, password, progress, checkpoint, retryAdmin, token);
        await Step("Wayfarer ready", config.Compose(root, "up", "-d", "--wait", "--wait-timeout", "180", "wayfarer"), null, token);
        if (config.Mode == "managed")
            await Step("Managed Caddy", config.Compose(root, "up", "-d", "--wait", "--wait-timeout", "180", "caddy"), null, token);
    }

    /// <summary>Never blindly repeat bootstrap after a lost result; application startup verifies admin security.</summary>
    private async Task BootstrapAsync(string root, Deployment config, string password, SetupProgress progress,
        Action? checkpoint, bool retryAdmin, CancellationToken token)
    {
        var found = false;
        if (progress.AdminStarted)
        {
            var lookup = await runner.RunAsync(config.Compose(root, "run", "--rm", "--no-deps", "-T", "wayfarer", "user", "find", "admin"), null, token);
            found = lookup.Code == 0;
            if (!found && !retryAdmin)
                throw new UsageException("Admin bootstrap outcome uncertain. Inspect logs; setup --resume --retry-admin explicitly retries transactional bootstrap with protected password input. Existing users are never replaced.");
        }
        if (!found)
        {
            progress.AdminStarted = true;
            checkpoint?.Invoke();
            await Step("Protected admin bootstrap", config.Compose(root, "run", "--rm", "--no-deps", "-T", "wayfarer", "admin", "bootstrap", "admin", "--stdin"), password, token);
        }
        progress.Completed = 3;
        checkpoint?.Invoke();
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
