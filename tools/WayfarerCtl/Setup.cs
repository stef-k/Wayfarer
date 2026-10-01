namespace WayfarerCtl;

/// <summary>Fresh-install coordinator with explicit continuation of verified, owned partial state.</summary>
public sealed class Setup(IProcessRunner runner, ITerminal terminal,
    Func<string, string, CancellationToken, Task<ReleaseBundle>>? acquire = null)
{
    private string phase = "checking this computer";
    private string remedy = "Check that supported Docker Engine and Docker Compose are installed and running.";
    private bool checkedRoot;

    /// <summary>Observe protected-file boundaries for focused interruption tests without substituting ownership checks.</summary>
    internal Action<string>? ProvisioningCheckpoint { get; init; }

    /// <summary>Recommend continuation only when a complete protected receipt can actually authorize it.</summary>
    internal enum Recovery { Retry, Resume, Reconcile }

    /// <summary>Parse a bounded setup surface; reject duplicate/unknown options before any host access.</summary>
    public static Dictionary<string, string> Options(string[] args)
    {
        var result = new Dictionary<string, string>();
        var valued = new[] { "--bundle", "--version", "--hostname", "--app-digest", "--mode", "--project", "--edge-prefix", "--loopback-port" };
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
        if (result.TryGetValue("--version", out var version) && !ReleaseContract.VersionSyntax(version))
            throw new UsageException("--version requires an exact stable X.Y.Z.");
        if (result.ContainsKey("--version") && result.ContainsKey("--bundle"))
            throw new UsageException("--version and --bundle are mutually exclusive.");
        if (result.ContainsKey("--app-digest") && !result.ContainsKey("--bundle"))
            throw new UsageException("Public setup derives image identities from release.json; --app-digest is only for local candidate qualification.");
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

    /// <summary>Prepare one retained release before entering the existing protected setup lifecycle.</summary>
    public async Task<int> RunAsync(string root, string[] args, CancellationToken token)
    {
        var options = Options(args);
        RestoreReceipt.RequireResolved(root);
        UpdateReceipt.RequireResolved(root);
        checkedRoot = false;
        try { return await RunCoreAsync(root, options, token); }
        catch (Exception error) { return ReportFailure(error, checkedRoot ? RecoveryForRoot(root) : Recovery.Retry); }
    }

    /// <summary>Preparation may retain releases; a complete initial input snapshot makes installation writes resumable.</summary>
    private async Task<int> RunCoreAsync(string root, Dictionary<string, string> options, CancellationToken token)
    {
        Stage("Checking this computer");
        remedy = "Check that this is a supported Linux computer with root access, Docker Engine and Docker Compose installed and running.";
        Preflight.Platform();
        ProtectedFiles.RequireRoot();
        if (options.ContainsKey("--resume")) return await ResumeAsync(root, options, token);
        remedy = "Correct the reported installation-folder or Docker prerequisite.";
        ProtectedFiles.SafePath(root);
        checkedRoot = true;
        if (Directory.Exists(root))
        {
            ProtectedFiles.Check(root, 0, directory: true);
            try { RequireFreshState(root); }
            catch (UsageException) when (!HasProtectedState(root))
            {
                terminal.Error("Wayfarer found unrecognized installation files. Nothing was overwritten. Preserve this folder and read 'wayfarerctl help setup' and the installation troubleshooting guide before continuing.");
                return 2;
            }
        }
        var preflight = new Preflight(runner);
        remedy = "Check that supported Docker Engine and Docker Compose are installed and running.";
        await preflight.DockerAsync(token);
        Stage("Preparing the Wayfarer download");
        remedy = "Check available disk space and protected installation-folder permissions.";
        ReleaseBundle? bundle;
        if (!options.ContainsKey("--bundle"))
        {
            Directory.CreateDirectory(root, ProtectedFiles.PrivateDirectory);
            ProtectedFiles.Check(root, 0, directory: true);
            using var preparation = Lock(root);
            bundle = await PrepareBundleAsync(root, options, token);
        }
        else
        {
            bundle = await PrepareBundleAsync(root, options, token);
            if (bundle is not null)
            {
                Directory.CreateDirectory(root, ProtectedFiles.PrivateDirectory);
                ProtectedFiles.Check(root, 0, directory: true);
                using var preparation = Lock(root);
                // Image probes execute only protected retained payloads, never the administrator's extraction directory.
                var retained = ReleaseStore.Import(root, bundle.Directory);
                if (retained.Fingerprint != bundle.Fingerprint) throw new IOException("Local bundle changed during setup preparation.");
                bundle = retained;
                Stage("Verifying required containers");
                remedy = "Make the trusted local release's required containers available and correct the reported verification cause.";
                if (!await new ReleaseImagesVerifier(runner).VerifyAsync(bundle, token))
                    throw new UsageException("Local setup requires the bundle's exact images already present and verified.");
            }
        }
        Stage("Checking installation choices");
        remedy = "Correct the reported setup choice or computer prerequisite.";
        var config = ReadChoices(options, bundle);
        config.CheckBundle();
        await preflight.FreshAsync(config, token);
        await preflight.BundleAsync(root, config, token);
        terminal.Write($"Preflight passed. Ready to install Wayfarer for {config.Hostname}.");
        var password = terminal.Password(options.ContainsKey("--password-stdin"));
        token.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        Directory.CreateDirectory(root, ProtectedFiles.PrivateDirectory);
        using var operationLock = Lock(root);
        // Recheck after taking the lock: another setup may have completed during preflight/password input.
        RequireFreshState(root);
        Stage("Preparing installation files");
        remedy = "Check available disk space and protected installation-folder permissions.";
        var progress = SetupProvisioning.Create(root, config, ProvisioningCheckpoint);
        return await FinishAsync(root, config, progress, password, false, token);
    }

    /// <summary>Only release preparation and the non-authoritative lock may precede another plain setup invocation.</summary>
    internal static void RequireFreshState(string root)
    {
        if (Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) is not ("releases" or "operation.lock")))
            throw new UsageException("Existing installation files prevent fresh setup; never overwrite them.");
    }

    /// <summary>Presence prevents fresh setup, including incomplete files and a committed provisioning snapshot.</summary>
    internal static bool HasProtectedState(string root) =>
        new[] { "installation.json", "deployment.env", "secrets", "setup-progress.json", SetupProvisioning.Name }
            .Any(name => Path.Exists(Path.Combine(root, name)) || new FileInfo(Path.Combine(root, name)).LinkTarget is not null);

    /// <summary>Read current authority after failure; uncertain or unreceipted protected state never grants continuation.</summary>
    internal static Recovery RecoveryForRoot(string root)
    {
        try
        {
            if (!HasProtectedState(root)) return Recovery.Retry;
            try { SetupProgress.Load(root, Deployment.Load(root)); }
            catch { SetupProvisioning.Load(root); }
            return Recovery.Resume;
        }
        catch { return Recovery.Reconcile; }
    }

    /// <summary>Continue only the protected original identity, under the same installation lock.</summary>
    private async Task<int> ResumeAsync(string root, Dictionary<string, string> options, CancellationToken token)
    {
        ProtectedFiles.SafePath(root);
        checkedRoot = true;
        ProtectedFiles.Check(root, 0, directory: true);
        using var operationLock = Lock(root);
        // The canonical receipt may be published before snapshot cleanup; verify either interruption boundary.
        if (Path.Exists(Path.Combine(root, SetupProvisioning.Name)))
        {
            Stage("Finishing installation files");
            remedy = "Check available disk space and protected installation-folder permissions.";
            SetupProvisioning.Resume(root, ProvisioningCheckpoint);
        }
        var config = Deployment.Load(root);
        if (InstallationCompletion.IsComplete(root))
        {
            terminal.Error("Setup is already complete. Installation state was retained. Run 'wayfarerctl doctor' to check this installation.");
            return 2;
        }
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
    internal async Task<int> FinishAsync(string root, Deployment config, SetupProgress progress, string password, bool retryAdmin, CancellationToken token)
    {
        try
        {
            await ExecuteAsync(root, config, password, token, progress, () => progress.Save(root), retryAdmin);
            Stage("Checking the installation");
            var result = await new Diagnostics(runner, terminal).RunAsync(root, config, true, token, finishingSetup: true);
            if (result != 0)
            {
                terminal.Error("Setup verification is incomplete. Setup has started; installation files and service data were retained. Correct the reported cause, then run 'wayfarerctl setup --resume'.");
                return result;
            }
            ProtectedFiles.Create(Path.Combine(root, "setup-complete"), "1\n");
            terminal.Write(config.Mode == "managed" ? "Setup complete: protected administrator and public HTTPS readiness verified." :
                "Setup complete: loopback readiness verified. External proxy TLS, forwarding and public reachability remain your responsibility.");
            return 0;
        }
        catch (Exception error) { return ReportFailure(error, Recovery.Resume); }
    }

    /// <summary>Skip committed maintenance; retry safe convergence steps and observe uncertain admin creation.</summary>
    public async Task ExecuteAsync(string root, Deployment config, string password, CancellationToken token,
        SetupProgress? progress = null, Action? checkpoint = null, bool retryAdmin = false)
    {
        progress ??= new SetupProgress();
        await Step("Checking installation settings", config.Compose(root, "config", "--quiet"), null, token);
        await Step("Preparing the database", config.Compose(root, "up", "-d", "--wait", "--wait-timeout", "180", "db"), null, token);
        // Fixed container volume roots only; never interpolate an administrator path into this script.
        await Step("Preparing application storage", config.Compose(root, "run", "--rm", "--no-deps", "-T", "--user", "0", "--entrypoint", "sh", "wayfarer", "-ec",
            "chown 1654:1654 /var/lib/wayfarer /var/cache/wayfarer /var/log/wayfarer; chmod 700 /var/lib/wayfarer; chmod 750 /var/cache/wayfarer /var/log/wayfarer"), null, token);
        foreach (var (number, operation) in new[] { (1, "migrate"), (2, "seed") })
        {
            if (progress.Completed >= number) continue;
            await Step(operation == "migrate" ? "Preparing database structure" : "Preparing initial application data",
                config.Compose(root, "run", "--rm", "--no-deps", "-T", "wayfarer", "database", operation), null, token);
            progress.Completed = number;
            checkpoint?.Invoke();
        }
        if (progress.Completed < 3)
            await BootstrapAsync(root, config, password, progress, checkpoint, retryAdmin, token);
        await Step("Starting Wayfarer", config.Compose(root, "up", "-d", "--wait", "--wait-timeout", "180", "wayfarer"), null, token);
        if (config.Mode == "managed")
            await Step("Starting HTTPS", config.Compose(root, "up", "-d", "--wait", "--wait-timeout", "180", "caddy"), null, token);
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
            await Step("Creating the administrator account", config.Compose(root, "run", "--rm", "--no-deps", "-T", "wayfarer", "admin", "bootstrap", "admin", "--stdin"), password, token);
        }
        progress.Completed = 3;
        checkpoint?.Invoke();
    }

    /// <summary>Mutation steps report their purpose but never retry or disclose captured child output.</summary>
    private async Task Step(string name, string[] command, string? input, CancellationToken token)
    {
        Stage(name);
        remedy = "Check that Docker is running and enough disk space is available, and correct the reported prerequisite.";
        if ((await runner.RunAsync(command, input, token)).Code != 0) throw new IOException("Setup step failed.");
    }

    /// <summary>Track a plain-language stage for unexpected setup failures without exposing exception text.</summary>
    private void Stage(string name)
    {
        phase = name.ToLowerInvariant();
        terminal.Write(name + "...");
    }

    /// <summary>Render safe owner messages with validated retry, continuation or explicit protected-state reconciliation.</summary>
    internal int ReportFailure(Exception error, Recovery recovery)
    {
        var reason = error is AcquisitionException acquisition ? acquisition.Message :
            error is OperationCanceledException ? $"Setup was cancelled while {phase}." : $"Wayfarer could not finish {phase}.";
        if (error is UsageException usage) reason += " " + usage.Message;
        var action = error is AcquisitionException safe ? safe.NextAction : remedy;
        terminal.Error(reason + "\n" + (recovery switch
        {
            Recovery.Resume => "Setup has started. Installation files, existing credentials and service data were retained.\n" + action + " Then run 'wayfarerctl setup --resume' with the same deployment-root option.",
            Recovery.Retry => "Setup has not started. No installation configuration or application data was changed.\n" + action + " Then run the same 'wayfarerctl setup' command again.",
            _ => "Wayfarer cannot safely resume this protected installation state. Files, credentials and service data were retained.\nPreserve this folder and have an administrator follow the protected-state reconciliation guidance in 'wayfarerctl help setup' and the installation troubleshooting guide."
        }));
        return error is UsageException ? 2 : 1;
    }

    /// <summary>Use the public acquisition owner or validate an explicit local bundle; neither path selects setup policy.</summary>
    internal async Task<ReleaseBundle?> PrepareBundleAsync(string root, Dictionary<string, string> options, CancellationToken token)
    {
        if (!options.TryGetValue("--bundle", out var path))
            return await (acquire ?? new PublicReleaseAcquisition(runner, terminal.Write).AcquireAsync)(root, options.GetValueOrDefault("--version", "latest"), token);
        BackupPolicy.LiteralPath(path);
        if (!File.Exists(Path.Combine(path, "release.json")))
        {
            // PG18 has no public DB pin yet; only canonical metadata can select its native manifest.
            throw new UsageException("Local setup requires a canonical release.json bundle.");
        }
        var bundle = ReleaseBundle.Validate(path);
        ReleaseContract.RequireUse(bundle.Manifest, ReleaseCommands.OperatorVersion);
        if (options.TryGetValue("--app-digest", out var digest) &&
            (bundle.Manifest.Status == "stable" || digest != bundle.Manifest.Images.PlatformDigest))
            throw new UsageException("Setup image identities are owned by validated release.json; stable digest overrides are unsupported.");
        return bundle;
    }

    /// <summary>Prompt only for administrator choices; canonical release metadata owns application, DB and pinned Caddy identity.</summary>
    internal Deployment ReadChoices(Dictionary<string, string> options, ReleaseBundle? bundle = null)
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
            Schema = bundle is null ? 1 : 4,
            Platform = bundle?.Manifest.Platform ?? WayfarerRecovery.NativePlatform.Current,
            Release = bundle is null ? null : ReleaseAuthority.From(bundle),
            Bundle = bundle?.Directory ?? options["--bundle"],
            Hostname = Choice("--hostname", "Public DNS hostname"),
            AppDigest = bundle?.Manifest.Images.PlatformDigest ?? options["--app-digest"],
            DbDigest = bundle?.Manifest.Images.DatabaseDigest ?? ReleaseContract.DatabaseDigest,
            Mode = mode, LoopbackPort = number,
            Project = options.GetValueOrDefault("--project", "wayfarer"),
            EdgePrefix = options.GetValueOrDefault("--edge-prefix", "172.30.64")
        };
    }
}
