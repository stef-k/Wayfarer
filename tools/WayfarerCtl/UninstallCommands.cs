using System.Security.Cryptography;
using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>One concrete command owner resolves planning choice, authorizes normal removal and deliberately reactivates preserved runtime.</summary>
public sealed class UninstallCommands(IProcessRunner runner, ITerminal terminal)
{
    /// <summary>Hosted tests use protected retained executable bytes; native execution still validates its actual process image.</summary>
    internal string? ExecutablePath { get; init; }

    /// <summary>Plan remains non-destructive; acceptance and replay share one exact hash under the established host lock.</summary>
    public async Task<int> RunAsync(string root, string[] args, CancellationToken token)
    {
        var options = UninstallOptions.Parse(args);
        options.CheckInteraction(terminal.Interactive);
        var preparation = new UninstallPreparation(runner) { ExecutablePath = ExecutablePath };
        if (options.Plan)
        {
            if (options.Mode == UninstallMode.Purge)
                terminal.Write("Purge planning selects destructive erasure of installation data/configuration. Backup storage and Docker images are excluded.");
            if (options.Backup is null)
            {
                _ = UninstallPreparation.RequireLifecycle(root);
                var config = Deployment.Load(root);
                if (config.Backup is not null) BackupCompose.Check(root, config);
                options = options with { Backup = ResolveBackup(config.Backup is { Enabled: true }) };
            }
            var plan = await preparation.PrepareAsync(root, options, token);
            terminal.Write(JsonSerializer.Serialize(plan));
            terminal.Write("Plan SHA-256: " + plan.Hash());
            terminal.Write(plan.Backup == UninstallBackup.Waived ? "Explicit waiver: no new recovery set will be created." :
                "A fresh quiesced capture and exact committed verification must succeed before uninstall authorization.");
            return 0;
        }
        return await AcceptAsync(root, options.Accept!, preparation, token);
    }

    /// <summary>Bounded interactive answers never infer a destructive waiver from Enter, EOF or unrecognized text.</summary>
    internal UninstallBackup ResolveBackup(bool usableBackup)
    {
        if (!terminal.Interactive) throw new UsageException("Redirected uninstall planning requires --backup or --without-backup.");
        var answer = terminal.Read(usableBackup
            ? "Create a fresh verified quiesced backup before uninstall? [Y/n]: "
            : "No enabled backup is available. Type WITHOUT BACKUP to continue planning, or press Enter to cancel: ");
        if (answer is null || !usableBackup && answer.Length == 0)
            throw new OperationCanceledException("Uninstall planning cancelled.");
        if (usableBackup)
        {
            if (answer?.ToLowerInvariant() is "" or "y" or "yes") return UninstallBackup.VerifiedQuiesced;
            if (answer?.ToLowerInvariant() is "n" or "no") return UninstallBackup.Waived;
        }
        else if (answer == "WITHOUT BACKUP") return UninstallBackup.Waived;
        throw new UsageException("Uninstall planning cancelled; no backup choice was authorized.");
    }

    /// <summary>Current accepted receipt owns interruption recovery; it never loads a competing plan or repeats final capture.</summary>
    private async Task<int> AcceptAsync(string root, string hash, UninstallPreparation preparation, CancellationToken token)
    {
        ProtectedFiles.RequireRoot();
        ProtectedFiles.SafePath(root);
        ProtectedFiles.Check(root, 0, directory: true);
        using var operation = Setup.Lock(root);
        UninstallReceipt? receipt = null;
        try
        {
            receipt = UninstallReceipt.Load(root);
            if (receipt is not null)
            {
                if (receipt.PlanHash != hash) throw new UsageException("Only the current uninstall receipt's exact accepted hash may be replayed.");
                _ = preparation.CheckReceiptAuthority(root, receipt);
                if (receipt.Plan.Mode != UninstallMode.Normal) throw new UsageException("Purge execution is not enabled in this handoff.");
                if (receipt.Phase == UninstallPhase.Preserved)
                {
                    terminal.Write("Already intentionally uninstalled; runtime removed and every volume retained. Use wayfarerctl start to reactivate.");
                    return 0;
                }
            }
            else
            {
                var plan = preparation.Load(root, hash);
                UninstallReceipt.RequireNewPlan(root, plan);
                if (plan.Mode == UninstallMode.Purge)
                    throw new UsageException("Purge execution is not enabled in Handoff 2; no backup, destructive receipt or Docker mutation was started.");
                await new Preflight(runner).DockerAsync(token);
                using (var recovery = RecoveryExclusion(root, plan.Current))
                    await preparation.RevalidateAsync(root, plan, token);
                var evidence = plan.Backup == UninstallBackup.VerifiedQuiesced
                    ? await FinalBackupAsync(root, plan.Current, token) : null;
                using var recoveryAfterBackup = RecoveryExclusion(root, plan.Current);
                await preparation.RevalidateAsync(root, plan, token);
                var authorized = new UninstallReceipt { Plan = plan, PlanHash = hash, FinalBackup = evidence };
                authorized.Save(root);
                receipt = authorized;
                return await RemoveAsync(root, receipt, preparation, token);
            }
            using var exclusion = RecoveryExclusion(root, receipt.Plan.Current);
            return await RemoveAsync(root, receipt, preparation, token);
        }
        catch (UsageException) when (receipt is null) { throw; }
        catch (Exception)
        {
            ReportFailure(root, receipt, false);
            return 1;
        }
    }

    /// <summary>Protected authority and exact Docker inventory are revalidated from the embedded plan after authorization.</summary>
    private async Task<int> RemoveAsync(string root, UninstallReceipt receipt, UninstallPreparation preparation, CancellationToken token)
    {
        var history = preparation.CheckReceiptAuthority(root, receipt);
        await new Preflight(runner).DockerAsync(token);
        receipt = await new UninstallRuntime(runner).RemoveAsync(root, receipt, history, token);
        terminal.Write($"Uninstall {receipt.Plan.Operation:D}; phase={receipt.Phase}; plan={receipt.PlanHash}. " +
            "Runtime containers/networks removed. Protected installation authority and every Docker volume retained. Use wayfarerctl start to reactivate.");
        return 0;
    }

    /// <summary>Capture through the existing quiesced worker, explicitly verify the exact committed basename and bind those same bytes.</summary>
    private async Task<UninstallBackupEvidence> FinalBackupAsync(string root, Deployment config, CancellationToken token)
    {
        var backup = new BackupCommands(runner, terminal);
        if (await backup.RunAsync(root, config, ["backup", "--quiesced"], token) != 0 ||
            backup.CaptureResult is not { Schema: 1, RetentionSucceeded: true } capture || capture.Archive == Guid.Empty ||
            capture.Completed.Offset != TimeSpan.Zero || capture.Name != ArchiveContract.Name(config.Installation, capture.Completed, capture.Archive))
            throw new IOException("Final quiesced capture did not succeed.");
        var committedHash = await CommittedHashAsync(config, capture, token);
        if (await backup.RunAsync(root, config, ["verify-backup", capture.Name], token) != 0 ||
            backup.VerificationResult is not { Schema: 1, IntegrityValid: true, CompatibilitySupported: true, Mode: "quiesced" } verified ||
            verified.Archive != capture.Archive || verified.Name != capture.Name)
            throw new IOException("Exact final backup verification did not succeed.");
        var verifiedHash = await CommittedHashAsync(config, capture, token);
        if (verifiedHash != committedHash) throw new IOException("Committed final backup bytes changed during verification.");
        return new(capture.Archive, capture.Name, verifiedHash, capture.Completed, true, true);
    }

    /// <summary>Reopen only the configured inode/marker and unique exact committed pair; this binds bytes without duplicating the archive verifier.</summary>
    private static async Task<string> CommittedHashAsync(Deployment config, BackupResult capture, CancellationToken token)
    {
        var policy = config.Backup!;
        using var destination = new SafeDirectory(policy.Destination);
        var facts = destination.Identity;
        if (facts.DeviceMajor != policy.DeviceMajor || facts.DeviceMinor != policy.DeviceMinor || facts.Inode != policy.Inode ||
            facts.User != 1654 || facts.Group != 1654 || (facts.Mode & 0x1ff) != 0x1c0)
            throw new IOException("Final backup destination identity changed.");
        if (policy.Kind == "mounted")
        {
            using var parent = new SafeDirectory(Path.GetDirectoryName(policy.Destination)!);
            if (facts.Mount == parent.Identity.Mount) throw new IOException("Final backup destination is unmounted.");
        }
        using var marker = destination.Read(".wayfarer-recovery");
        if (marker.Length > 128 || new StreamReader(marker).ReadToEnd() != $"wayfarer-recovery-v1\n{config.Installation:D}\n")
            throw new IOException("Final backup destination installation marker changed.");
        var names = destination.Names(4096);
        var matches = names.Where(name => name.EndsWith("_" + capture.Archive.ToString("D") + ".tar", StringComparison.Ordinal)).ToArray();
        if (matches.Length != 1 || matches[0] != capture.Name || !names.Contains(capture.Name + ".sha256"))
            throw new IOException("Final backup publication is not one exact committed pair.");
        using var archive = destination.Read(capture.Name);
        if (archive.Length > ArchiveContract.ByteLimit) throw new IOException("Final backup exceeds its byte bound.");
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, token));
        using var sidecar = destination.Read(capture.Name + ".sha256");
        if (sidecar.Length > 512 || new StreamReader(sidecar).ReadToEnd() != $"{hash}  {capture.Name}\n")
            throw new IOException("Final backup committed checksum changed.");
        return hash;
    }

    /// <summary>Preserved status/doctor avoid outage diagnosis; only start may recreate canonical runtime after retained-volume proof.</summary>
    internal async Task<int> PreservedAsync(string root, string command, CancellationToken token)
    {
        if (command is not ("status" or "doctor" or "start" or "stop"))
            throw new UsageException(command == "restart" ? "Wayfarer is intentionally uninstalled; use wayfarerctl start deliberately, rather than restart." :
                "Wayfarer is intentionally uninstalled; this command is unavailable until wayfarerctl start completes.");
        ProtectedFiles.RequireRoot();
        ProtectedFiles.SafePath(root);
        ProtectedFiles.Check(root, 0, directory: true);
        using var operation = command == "start" ? Setup.Lock(root) : null;
        var receipt = UninstallReceipt.Load(root) ?? throw new UsageException("Missing preserved uninstall receipt.");
        if (receipt.Phase != UninstallPhase.Preserved || receipt.Plan.Mode != UninstallMode.Normal)
            throw new UsageException("A terminal matching normal-uninstall receipt is required.");
        try
        {
            var preparation = new UninstallPreparation(runner) { ExecutablePath = ExecutablePath };
            var history = preparation.CheckReceiptAuthority(root, receipt);
            if (command == "stop")
            {
                terminal.Write("Runtime is already removed; all Docker volumes are retained.");
                return 0;
            }
            terminal.Write("Wayfarer is intentionally uninstalled (Preserved). Uninstall removed runtime containers/networks. " +
                "Protected installation authority and Docker volumes are retained. Use wayfarerctl start to reactivate or finish interrupted reactivation.");
            if (command == "status") return 0;
            using var recovery = command == "start" ? RecoveryExclusion(root, receipt.Plan.Current) : null;
            await new Preflight(runner).DockerAsync(token);
            var runtime = new UninstallRuntime(runner);
            await runtime.ValidateAsync(root, receipt, history, true, token);
            if (command == "doctor")
            {
                terminal.Write("PASS Preserved protected authority, retained release/operator, lifecycle history and exact retained volume identities.");
                return 0;
            }
            if (await new DeploymentLifecycle(runner, terminal).RunAsync(root, receipt.Plan.Current, "start", token) != 0)
                throw new IOException("Reactivated runtime health is not proven.");
            history = preparation.CheckReceiptAuthority(root, receipt);
            await runtime.ValidateAsync(root, receipt, history, true, token);
            receipt.Retire(root);
            terminal.Write("Reactivation complete; healthy runtime uses retained storage. Uninstall receipt archived; ordinary active lifecycle restored.");
            return 0;
        }
        catch (Exception)
        {
            ReportFailure(root, receipt, command == "start");
            return 1;
        }
    }

    /// <summary>Configured recovery uses the existing exclusion inode; an installation without backup needs no new lock/control tree.</summary>
    private static RecoveryLock? RecoveryExclusion(string root, Deployment config) => config.Backup is null ? null :
        new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));

    /// <summary>Only locally generated operation/hash/phase reach the terminal; child diagnostics and exception text stay private.</summary>
    private void ReportFailure(string root, UninstallReceipt? receipt, bool reactivating)
    {
        try { receipt = UninstallReceipt.Load(root) ?? receipt; }
        catch { /* The last validated receipt still identifies the unresolved operation without printing malformed bytes. */ }
        if (receipt is null)
            terminal.Error("Uninstall was not authorized; no uninstall removal started. A quiesced backup may have left the application stopped. " +
                "Resolve ordinary backup reservation/recovery state, then use wayfarerctl start when appropriate.");
        else terminal.Error($"Uninstall {receipt.Plan.Operation:D} incomplete; durable phase={receipt.Phase}; accepted plan={receipt.PlanHash}. " +
            (reactivating ? "Retained receipt preserved; retry wayfarerctl start to finish reactivation." :
                $"Replay wayfarerctl uninstall --accept-plan {receipt.PlanHash}."));
    }
}
