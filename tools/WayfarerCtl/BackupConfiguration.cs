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
        if (!int.TryParse(result.GetValueOrDefault("--retention", "7"), out var retention) || retention is < 1 or > 100 ||
            result.GetValueOrDefault("--kind", "local") is not ("local" or "mounted") ||
            !TimeOnly.TryParseExact(result.GetValueOrDefault("--time", "03:00"), "HH:mm", out _))
            throw new UsageException("Invalid retention, destination kind or UTC schedule.");
        BackupPolicy.LiteralPath(result["--destination"]); BackupPolicy.LiteralPath(result["--payload"]);
        return result;
    }

    /// <summary>Stop the owned scheduler, acquire recovery exclusion and commit one complete generation.</summary>
    public async Task<Deployment> ConfigureAsync(string root, Deployment config, string[] args, CancellationToken token)
    {
        if (!File.Exists(Path.Combine(root, "setup-complete"))) throw new UsageException("Backup configuration requires completed setup.");
        var options = Options(args);
        ProvisionControl(root, config.Backup is null);
        using var exclusion = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
        if (config.Backup is not null)
            await Required(BackupCompose.Command(root, config, "stop", "--timeout", "30", "backup-scheduler"), token);
        if (options.ContainsKey("--recover"))
        {
            if (File.Exists(Path.Combine(root, "backup-transition.json"))) BackupGeneration.Recover(root);
            var reservationPath = Path.Combine(root, "recovery-control/host-operation.json");
            if (File.Exists(reservationPath))
            {
                var receipt = JsonSerializer.Deserialize<HostRecoveryOperation>(File.ReadAllText(reservationPath), ArchiveContract.Json)
                    ?? throw new UsageException("Invalid recovery reservation.");
                if (!System.Text.RegularExpressions.Regex.IsMatch(receipt.Container, "^" + System.Text.RegularExpressions.Regex.Escape(config.Project) + "-backup-[a-f0-9]{32}$"))
                    throw new UsageException("Unknown recovery container identity.");
                var running = await runner.RunAsync(["ps", "-q", "--filter", "name=^/" + receipt.Container + "$"], null, token);
                if (running.Code != 0 || running.Output.Trim().Length != 0) throw new IOException("Recovery worker running or state unknown.");
                File.Delete(reservationPath);
            }
            return Deployment.Load(root);
        }
        if (File.Exists(Path.Combine(root, "recovery-control/host-operation.json"))) throw new IOException("Unresolved recovery operation; use configure --recover.");
        var identityPath = Path.Combine(root, "backup-identity");
        if (config.Installation == Guid.Empty && !File.Exists(identityPath)) ProtectedFiles.Create(identityPath, Guid.NewGuid().ToString("D"));
        if (config.Installation == Guid.Empty) ProtectedFiles.Check(identityPath, 0);
        var installation = config.Installation == Guid.Empty ? Guid.Parse(File.ReadAllText(identityPath)) : config.Installation;
        BackupPolicy policy;
        if (options.ContainsKey("--disable")) policy = (config.Backup ?? throw new UsageException("Backup is not configured.")) with { Enabled = false };
        else
        {
            if (!int.TryParse(options.GetValueOrDefault("--retention", "7"), out var retention) || retention is < 1 or > 100)
                throw new UsageException("Retention must be 1..100.");
            var time = options.GetValueOrDefault("--time", "03:00");
            if (!TimeOnly.TryParseExact(time, "HH:mm", out var daily)) throw new UsageException("Daily time must be HH:mm UTC.");
            var payload = options["--payload"];
            ProtectedFiles.SafePath(payload);
            policy = new BackupPolicy
            {
                Destination = options["--destination"], Kind = options.GetValueOrDefault("--kind", "local"), Payload = payload,
                PayloadSha256 = BackupPolicy.Fingerprint(payload), Retention = retention, DailyMinute = daily.Hour * 60 + daily.Minute
            };
            await new Preflight(runner).BundleAsync(root, config, token);
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
                    PayloadFingerprint = policy.PayloadSha256, WorkerVersion = typeof(WorkerConfiguration).Assembly.GetName().Version!.ToString()
                } };
        }
        policy = policy with { Generation = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) };
        var next = config with { Schema = 2, Installation = installation, Backup = policy };
        next.Validate(); policy.CheckPayload();
        BackupGeneration.Stage(root, next);
        BackupCompose.Check(root, next);
        await Required(BackupCompose.Command(root, next, "config", "--quiet"), token);
        if (policy.Enabled) await CheckDestinationAsync(root, next, token);
        BackupGeneration.Commit(root, next);
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
        var image = await runner.RunAsync(["image", "inspect", "ghcr.io/stef-k/wayfarer@" + config.AppDigest,
            "--format", "{{index .Config.Labels \"org.opencontainers.image.revision\"}}"], null, token);
        var revision = image.Output.Trim();
        if (image.Code != 0 || !System.Text.RegularExpressions.Regex.IsMatch(revision, "^[a-f0-9]{40}$"))
            throw new UsageException("Immutable application source revision unavailable.");
        var values = document.RootElement.Deserialize<Dictionary<string, JsonElement>>()!;
        values["SourceRevision"] = JsonSerializer.SerializeToElement(revision);
        return JsonSerializer.SerializeToElement(values);
    }

    /// <summary>Prepare only this dedicated empty destination, never recursively change administrator content.</summary>
    private static SafeDirectory.Facts Destination(string root, Deployment config, BackupPolicy policy, Guid installation)
    {
        BackupPolicy.LiteralPath(policy.Destination);
        foreach (var forbidden in new[] { root, config.Bundle, "/var/lib/docker", "/var/lib/wayfarer", "/var/cache/wayfarer", "/var/log/wayfarer", "/etc" })
            if (policy.Destination == forbidden || policy.Destination.StartsWith(forbidden + "/", StringComparison.Ordinal))
                throw new UsageException("Destination overlaps protected host/application state.");
        ProtectedFiles.SafePath(Path.GetDirectoryName(policy.Destination)!);
        using var destination = new SafeDirectory(policy.Destination);
        if (policy.Kind == "mounted")
        {
            using var parent = new SafeDirectory(Path.GetDirectoryName(policy.Destination)!);
            if (parent.Identity.User != 0 || parent.Identity.Group != 0 || (parent.Identity.Mode & 0x1ff) != 0x1ed ||
                Path.GetFileName(policy.Destination) != "slot" || !parent.Names().SequenceEqual(new[] { "slot" }) ||
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
    private static void ProvisionControl(string root, bool bootstrap)
    {
        var directory = Path.Combine(root, "recovery-control");
        if (!Directory.Exists(directory)) Directory.CreateDirectory(directory, ProtectedFiles.PrivateDirectory);
        using var control = new SafeDirectory(directory);
        control.RequireLocalControl();
        // Bootstrap may resume after individual creates, but never repairs an existing unsafe lock or parent.
        if (bootstrap && control.Identity.User == 0 && (control.Identity.Mode & 0x1ff) == 0x1c0)
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        if (control.Identity.User != 0 || control.Identity.Group != 0 || (control.Identity.Mode & 0x1ff) != 0x1ed)
            throw new IOException("Recovery control parent must be root-owned mode 0755.");
        var lockPath = Path.Combine(directory, "recovery.lock");
        if (bootstrap && !Path.Exists(lockPath))
        {
            ProtectedFiles.Create(lockPath, "", 0, 1654);
            File.SetUnixFileMode(lockPath, ProtectedFiles.PrivateFile | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
        }
        using (var file = control.Read("recovery.lock"))
        {
            var facts = SafeDirectory.Inspect(file.SafeFileHandle);
            if (facts.User != 0 || facts.Group != 1654 || (facts.Mode & 0x1ff) != 0x1b0)
                throw new IOException("Unsafe recovery lock ownership.");
        }
        var state = Path.Combine(directory, "state");
        if (bootstrap && !Path.Exists(state))
        {
            Directory.CreateDirectory(state, ProtectedFiles.PrivateDirectory);
            if (chown(state, 1654, 1654) != 0) throw new IOException("Cannot provision scheduler state.");
        }
        ProtectedFiles.Check(state, 1654, directory: true);
        control.Flush();
    }

    internal static string BundleFingerprint(Deployment config)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "compose.yaml", "external.yaml", "caddy/Caddyfile", "db/20-wayfarer.sh" })
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(Path.Combine(config.Bundle, name))));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Own the actual preflight container so cancelled configuration cannot leave a destination writer behind.</summary>
    private async Task CheckDestinationAsync(string root, Deployment config, CancellationToken token)
    {
        var name = config.Project + "-destination-check-" + Guid.NewGuid().ToString("N");
        Exception? primary = null;
        try
        {
            await Required(BackupCompose.Command(root, config, "run", "-d", "--no-deps", "--name", name, BackupCompose.ServiceFor("destination-check"), "destination-check"), token);
            var result = await runner.RunAsync(["wait", name], null, token);
            if (result.Code != 0 || result.Output.Trim() != "0") throw new UsageException("Destination write/flush/rename/read/delete capability failed.");
        }
        catch (Exception error) { primary = error; throw; }
        finally
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try
            {
                await runner.RunAsync(["stop", "--time", "30", name], null, cleanup.Token);
                var waited = await runner.RunAsync(["wait", name], null, cleanup.Token);
                if (waited.Code == 0) await runner.RunAsync(["rm", name], null, cleanup.Token);
                else throw new IOException("Destination preflight state unknown.");
            }
            catch (Exception)
            {
                const string warning = "Destination preflight state unknown; inspect owned container before retry.";
                if (primary is null) throw new IOException(warning);
                Console.Error.WriteLine(warning);
            }
        }
    }

    private async Task Required(string[] arguments, CancellationToken token)
    {
        if ((await runner.RunAsync(arguments, null, token)).Code != 0) throw new IOException("Backup Compose operation failed.");
    }
}
