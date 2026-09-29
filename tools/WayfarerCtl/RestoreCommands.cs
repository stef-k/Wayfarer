using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Administrator-facing restore coordination; archive integrity never implies authorization.</summary>
public sealed class RestoreCommands(IProcessRunner runner, ITerminal terminal)
{
    /// <summary>Plan before mutation, acquire locks in host/recovery order, and retain truthful durable failure phase.</summary>
    public async Task<int> RunAsync(string root, string[] args, CancellationToken token)
    {
        try { return await DispatchAsync(root, args, token); }
        catch (UsageException) { throw; }
        catch (JsonException) { throw; }
        catch (Exception)
        {
            var outcome = "unknown; inspect status/doctor";
            try
            {
                var intent = RestoreReceipt.Load(root);
                outcome = intent is null || intent.Phase is RestorePhase.Accepted or RestorePhase.Aborted
                    ? "unchanged" : intent.Phase.ToString();
            }
            catch { /* Invalid intent must never be reported as unchanged. */ }
            terminal.Error("Restore incomplete; phase=" + outcome + ".");
            return 1;
        }
    }

    /// <summary>Selection and confirmation precede durable mutation; redirected input cannot authorize implicitly.</summary>
    private async Task<int> DispatchAsync(string root, string[] args, CancellationToken token)
    {
        var options = RestoreOptions.Parse(args);
        ProtectedFiles.SafePath(root);
        if (!Directory.Exists(root)) Directory.CreateDirectory(root, ProtectedFiles.PrivateDirectory);
        ProtectedFiles.Check(root, 0, directory: true);
        using var operation = Setup.Lock(root);
        await new Preflight(runner).DockerAsync(token);
        if (options.Has("--resume") || options.Has("--abort")) return await RecoverAsync(root, options, token);
        RestoreReceipt.RequireResolved(root);
        UpdateReceipt.RequireResolved(root);
        RestorePlan plan;
        if (options.Has("--accept-plan")) plan = LoadPlan(root, options.Get("--accept-plan"));
        else
        {
            var config = options.Has("--new-install") ? NewTarget(root, options) : Deployment.Load(root);
            if (!options.Has("--new-install") && !InstallationCompletion.IsComplete(root)) throw new UsageException("Restore requires a completed installation.");
            config = WithRestoreIdentity(config, Guid.NewGuid());
            if (options.Has("--new-install"))
            {
                await new Preflight(runner).FreshAsync(config, token);
                await new Preflight(runner).BundleAsync(root, config, token);
            }
            plan = await new RestorePreparation(runner).PrepareAsync(root, config, options, token);
        }
        options.CheckPlan(plan);
        terminal.Write(JsonSerializer.Serialize(plan));
        if (plan.CaptureMode == "online") terminal.Write("Online archive: component capture is not a cross-component transactional snapshot.");
        terminal.Write("Plan SHA-256: " + plan.Hash() + "\nLater writes will be discarded. Candidate writer launch is the rollback cutoff.");
        if (options.Has("--plan")) return 0;
        if (!options.Has("--trust-controlled-backup"))
        {
            if (!terminal.Interactive) throw new UsageException("Explicit --trust-controlled-backup provenance acknowledgement is required before SQL execution.");
            if (terminal.Read("Checksums do not authenticate this archive. Type I TRUST THIS BACKUP to acknowledge controlled custody: ") != "I TRUST THIS BACKUP")
            {
                terminal.Write("Restore cancelled; phase=unchanged.");
                return 1;
            }
        }
        if (!options.Has("--accept-plan"))
        {
            if (!terminal.Interactive) throw new UsageException("Redirected execution requires --accept-plan SHA256.");
            var expected = $"RESTORE {plan.Target.Installation:D} {plan.Archive:D}";
            if (terminal.Read("Type " + expected + ": ") != expected) { terminal.Write("Restore cancelled; authoritative state unchanged."); return 1; }
        }
        await RevalidateAsync(root, plan, token);
        BackupConfiguration.ProvisionControl(root, true);
        var receipt = new RestoreReceipt { Plan = plan, PlanHash = plan.Hash(), SecretsFingerprint = plan.LocalSecretsFingerprint };
        using (var exclusion = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock")))
        {
            if (File.Exists(Path.Combine(root, "recovery-control/host-operation.json"))) throw new IOException("Active recovery reservation.");
            RestoreReceipt.ArchiveResolved(root);
            receipt.Save(root);
        }
        return await ExecuteAsync(root, receipt, token);
    }

    /// <summary>Explicit failed-update recovery binds the held archive to retained old authority before granting restore ownership.</summary>
    internal async Task<int> HandoffAsync(string root, UpdateReceipt update, CancellationToken token)
    {
        if (!update.MigrationPossible) throw new UsageException("Before migration use update abort.");
        await new UpdateRuntime(runner).StopWritersAsync(root, update, token);
        if (update.MigrationContainer is not null)
        {
            var owner = new RestoreContainers(runner);
            using var inspection = JsonDocument.Parse(await owner.Required(["inspect", update.MigrationContainer], token));
            if (inspection.RootElement[0].GetProperty("Id").GetString() != update.MigrationContainerId ||
                inspection.RootElement[0].GetProperty("State").GetProperty("Running").GetBoolean())
                throw new IOException("Migration helper must terminate before restore handoff.");
        }
        await UpdateCommands.VerifyRecoveryAsync(update, token);
        RestoreReceipt receipt;
        using (var recovery = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock")))
        {
            var existing = RestoreReceipt.Load(root);
            if (update.RestoreOperation is not null && existing?.Plan.Operation == update.RestoreOperation)
            {
                UpdateRestoreHandoff.Require(root, existing.Plan);
                receipt = existing;
            }
            else
            {
                RestoreReceipt.RequireResolved(root);
                var path = Path.Combine(update.Plan.Current.Backup!.Destination, update.RecoveryName!);
                var options = RestoreOptions.Parse(["--archive", path, "--source-installation", update.Plan.Current.Installation.ToString("D"),
                    "--without-emergency-backup", "--trust-controlled-backup", "--plan"]);
                var plan = await new RestorePreparation(runner).PrepareAsync(root, update.Plan.Current, options, token);
                plan = plan with { FromUpdate = update.Plan.Operation };
                receipt = new RestoreReceipt { Plan = plan, PlanHash = plan.Hash(), SecretsFingerprint = plan.LocalSecretsFingerprint,
                    Phase = RestorePhase.Fenced, Containers = update.Containers, RestartPolicies = update.RestartPolicies };
                update = update with { RestoreOperation = plan.Operation };
                update.Save(root);
                RestoreReceipt.ArchiveResolved(root);
                receipt.Save(root);
            }
        }
        if (receipt.Phase == RestorePhase.Accepted)
        {
            UpdateRestoreHandoff.Complete(root, receipt);
            return 0;
        }
        return await ExecuteAsync(root, receipt, token);
    }

    /// <summary>Restore alone assigns an absent UUID; adoption preserves legacy absence and the release schema.</summary>
    internal static Deployment WithRestoreIdentity(Deployment config, Guid installation) => config.Installation == Guid.Empty
        ? config with { Schema = Math.Max(2, config.Schema), Installation = installation } : config;

    /// <summary>Clean-root restoration reuses setup choices and preflight without invoking setup stages.</summary>
    private Deployment NewTarget(string root, RestoreOptions options)
    {
        if (Directory.EnumerateFileSystemEntries(root).Any(path => Path.GetFileName(path) is not ("operation.lock" or "restore-plans" or "releases")))
            throw new UsageException("New-install restore requires a clean target.");
        var config = new Setup(runner, terminal).ReadChoices(options.Values) with
        {
            Schema = 2, Installation = Guid.NewGuid(), DbDigest = options.Get("--db-digest")
        };
        config.CheckBundle();
        return config;
    }

    /// <summary>Only protected plans generated by this operator can supply target UUID or candidate generation.</summary>
    private static RestorePlan LoadPlan(string root, string hash)
    {
        var parent = Path.Combine(root, "restore-plans");
        foreach (var directory in Directory.EnumerateDirectories(parent).Take(101))
        {
            var path = Path.Combine(directory, "plan.json");
            if (!File.Exists(path)) continue;
            ProtectedFiles.SafePath(path);
            ProtectedFiles.Check(path, 0);
            if (new FileInfo(path).Length > 262144) throw new UsageException("Plan exceeds bound.");
            var plan = JsonSerializer.Deserialize<RestorePlan>(File.ReadAllText(path), ArchiveContract.Json);
            if (plan is not null && plan.Root == root && plan.Hash() == hash) return plan;
        }
        throw new UsageException("No matching protected restore plan.");
    }

    /// <summary>Bind execution to unchanged target authority and reverify only frozen bytes after interruption.</summary>
    private async Task RevalidateAsync(string root, RestorePlan plan, CancellationToken token)
    {
        ReleaseDispatch.RequireOwner(root, plan);
        if (plan.NewInstall)
        {
            if (File.Exists(Path.Combine(root, "installation.json")) || Directory.Exists(Path.Combine(root, "secrets")))
                throw new UsageException("Clean target changed since planning.");
            await new Preflight(runner).FreshAsync(plan.Target, token);
        }
        else
        {
            var current = Deployment.Load(root);
            current = WithRestoreIdentity(current, plan.Target.Installation);
            if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(plan.Target))
                throw new UsageException("Installation changed since planning.");
        }
        if (!plan.NewInstall && ProtectedFiles.SecretsFingerprint(root) != plan.LocalSecretsFingerprint)
            throw new UsageException("Local credentials changed since planning.");
        var directory = RestorePreparation.DirectoryFor(root, plan.Operation);
        var payload = File.ReadAllText(Path.Combine(directory, "payload"));
        if (BackupPolicy.Fingerprint(payload) != plan.RestorePayloadFingerprint ||
            BackupConfiguration.BundleFingerprint(plan.Target) != plan.BundleFingerprint) throw new UsageException("Trusted payload/bundle changed.");
        var name = ArchiveContract.Name(plan.SourceInstallation, plan.Captured, plan.Archive);
        using var frozen = new SafeDirectory(Path.Combine(directory, "frozen"));
        using var archive = frozen.Read(name);
        if (Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(archive, token)) != plan.ArchiveSha256)
            throw new UsageException("Frozen archive changed.");
        await new RestorePreparation(runner).VerifyAsync(plan.Target, directory, plan.Operation, payload, name, plan.SourceInstallation, token);
    }

    /// <summary>Only an authorized new-root receipt may finish local file provisioning after interruption.</summary>
    private static void InitializeNewTarget(string root, Deployment config)
    {
        foreach (var (name, content) in new[] { ("installation.json", JsonSerializer.Serialize(config)),
            ("deployment.env", config.EnvironmentFile(root)) })
        {
            var path = Path.Combine(root, name);
            if (!File.Exists(path)) ProtectedFiles.Create(path, content);
            ProtectedFiles.Check(path, 0);
            if (File.ReadAllText(path) != content) throw new UsageException("New restore target configuration changed.");
        }
        ProtectedFiles.ResumeRestoreSecrets(root);
        using var directory = new SafeDirectory(root);
        directory.Flush();
    }

    /// <summary>Emergency capture delegates to the existing lock-taking worker while host serialization and intent remain held.</summary>
    private async Task<int> ExecuteAsync(string root, RestoreReceipt receipt, CancellationToken token)
    {
        try
        {
            ReleaseDispatch.RequireOwner(root, receipt.Plan);
            if (receipt.Plan.NewInstall && receipt.Phase == RestorePhase.Authorized)
            {
                InitializeNewTarget(root, receipt.Plan.Target);
                var fingerprint = ProtectedFiles.SecretsFingerprint(root);
                if (receipt.SecretsFingerprint is not null && receipt.SecretsFingerprint != fingerprint)
                    throw new UsageException("New restore credentials changed after provisioning.");
                receipt = receipt with { SecretsFingerprint = fingerprint };
                receipt.Save(root);
            }
            if (BackupConfiguration.BundleFingerprint(receipt.Plan.Target) != receipt.Plan.BundleFingerprint)
                throw new UsageException("Trusted bundle changed during restore.");
            if (receipt.Plan.FromUpdate is not null) UpdateRestoreHandoff.Require(root, receipt.Plan);
            if (receipt.Phase < RestorePhase.ActivationIntent && receipt.Plan.FromUpdate is null)
            {
                var current = Deployment.Load(root);
                current = WithRestoreIdentity(current, receipt.Plan.Target.Installation);
                if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(receipt.Plan.Target))
                    throw new UsageException("Installation changed during restore.");
            }
            if (ProtectedFiles.SecretsFingerprint(root) != receipt.SecretsFingerprint)
                throw new UsageException("Local credentials changed during restore.");
            await new RestoreCapacity(runner).CheckAsync(root, receipt, token);
            if (receipt.Phase == RestorePhase.Authorized)
            using (var exclusion = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock")))
            {
                receipt = await new RestoreFencing(runner).FenceAsync(root, receipt, token);
                receipt = receipt.Advance(RestorePhase.Fenced);
                receipt.Save(root);
            }
            if (receipt.Phase == RestorePhase.Fenced && !receipt.Plan.NewInstall && !receipt.Plan.WithoutEmergencyBackup && receipt.EmergencyArchive is null)
            {
                await new RestoreFencing(runner).StartEmergencyDatabaseAsync(root, receipt, token);
                var capture = new BackupCommands(runner, terminal);
                if (await capture.RunAsync(root, receipt.Plan.Target, ["backup", "--quiesced"], token, restoreEmergency: true) != 0 || capture.CompletedArchive is null)
                    throw new IOException("Fresh emergency capture failed.");
                receipt = (RestoreReceipt.Load(root) ?? receipt) with { EmergencyArchive = capture.CompletedArchive };
            }
            using var recovery = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
            if (receipt.Phase == RestorePhase.Fenced)
            {
                receipt = receipt.Advance(RestorePhase.EmergencyVerifiedOrWaived);
                receipt.Save(root);
            }
            await new RestoreFencing(runner).StopOwnedAsync(receipt, token);
            if (receipt.Phase == RestorePhase.EmergencyVerifiedOrWaived)
            {
                receipt = receipt.Advance(RestorePhase.Staging);
                receipt.Save(root);
            }
            if (receipt.Phase == RestorePhase.Staging)
            {
                receipt = await new RestoreCandidate(runner).StageAsync(root, receipt, token);
                receipt = receipt.Advance(RestorePhase.CandidateValidated);
                receipt.Save(root);
            }
            await new RestoreCapacity(runner).CheckAsync(root, receipt, token);
            receipt = await new RestoreActivation(runner).ActivateAsync(root, receipt, token);
            UpdateRestoreHandoff.Complete(root, receipt);
            terminal.Write($"Protected provider credentials: {receipt.ProtectedCredentialStatus}.");
            terminal.Write($"Restore accepted: {receipt.Plan.Operation:D}. Old volumes and emergency evidence retained.");
            return 0;
        }
        catch (Exception error)
        {
            if (error is UsageException || error is IOException && error.TargetSite?.DeclaringType?.Namespace == "WayfarerCtl")
                terminal.Error(error.Message);
            receipt = RestoreReceipt.Load(root) ?? receipt;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            var runtime = receipt.Phase == RestorePhase.Authorized && receipt.Containers.Length == 0 ? "unchanged" : "fenced";
            try { await new RestoreFencing(runner).StopOwnedAsync(receipt, cleanup.Token); }
            catch { runtime = "unknown"; terminal.Error("Restore cleanup uncertain; durable intent retained."); }
            terminal.Error($"Restore incomplete: {receipt.Plan.Operation:D}; phase={receipt.Phase}; writes-possible={receipt.WritesPossible}; runtime={runtime}. Reconciliation required.");
            return 1;
        }
    }

    /// <summary>Recovery operates only on exact protected intent; no writer-cutoff rollback is inferred.</summary>
    private async Task<int> RecoverAsync(string root, RestoreOptions options, CancellationToken token)
    {
        var receipt = RestoreReceipt.Load(root) ?? throw new UsageException("No restore receipt.");
        ReleaseDispatch.RequireOwner(root, receipt.Plan);
        var id = options.Get(options.Has("--abort") ? "--abort" : "--resume");
        if (receipt.Plan.Operation.ToString("D") != id) throw new UsageException("Restore operation mismatch.");
        if (receipt.Phase == RestorePhase.Accepted && receipt.Plan.FromUpdate is not null)
        {
            UpdateRestoreHandoff.Complete(root, receipt);
            return 0;
        }
        if (receipt.Phase is RestorePhase.Accepted or RestorePhase.Aborted) throw new UsageException("Restore already resolved.");
        await new RestoreFencing(runner).StopOwnedAsync(receipt, token);
        using (var recovery = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock")))
        {
            var reservationPath = Path.Combine(root, "recovery-control/host-operation.json");
            if (File.Exists(reservationPath))
            {
                var reservation = JsonSerializer.Deserialize<HostRecoveryOperation>(File.ReadAllText(reservationPath), ArchiveContract.Json)
                    ?? throw new IOException("Invalid restore delegation.");
                if (!reservation.RestoreHold || !receipt.Containers.Contains(reservation.Container))
                    throw new IOException("Foreign recovery reservation prevents reconciliation.");
                File.Delete(reservationPath);
                using var control = new SafeDirectory(Path.GetDirectoryName(reservationPath)!);
                control.Flush();
            }
        }
        if (options.Has("--abort"))
        {
            if (receipt.Plan.FromUpdate is not null) throw new UsageException("Update-owned restore cannot abort into mutated old storage; resume restore.");
            if (receipt.WritesPossible) throw new UsageException("Writes may have occurred; explicit recovery is required.");
            if (receipt.Phase >= RestorePhase.ActivationIntent)
            {
                if (receipt.OldConfiguration is null) throw new IOException("Previous authority unavailable.");
                var old = JsonSerializer.Deserialize<Deployment>(receipt.OldConfiguration)!;
                if (old.StorageGeneration is null)
                {
                    var path = Path.Combine(root, "installation.json.abort");
                    ProtectedFiles.Create(path, receipt.OldConfiguration);
                    File.Move(path, Path.Combine(root, "installation.json"), true);
                    using var directory = new SafeDirectory(root);
                    directory.Flush();
                }
                else RestoreActivation.Commit(root, old);
                if (!receipt.Plan.NewInstall)
                    await new RestoreContainers(runner).Required(old.Compose(root, "create", "--force-recreate", "--pull", "never", "db", "wayfarer"), token);
            }
            receipt.Advance(RestorePhase.Aborted).Save(root);
            terminal.Write("Restore aborted before writer cutoff. Prior authority retained; services remain stopped.");
            return 0;
        }
        var directoryPath = RestorePreparation.DirectoryFor(root, receipt.Plan.Operation);
        var payload = File.ReadAllText(Path.Combine(directoryPath, "payload"));
        if (BackupPolicy.Fingerprint(payload) != receipt.Plan.RestorePayloadFingerprint)
            throw new UsageException("Restore payload changed during interruption.");
        var archiveName = ArchiveContract.Name(receipt.Plan.SourceInstallation, receipt.Plan.Captured, receipt.Plan.Archive);
        using (var frozen = new SafeDirectory(Path.Combine(directoryPath, "frozen")))
        using (var archive = frozen.Read(archiveName))
            if (Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(archive, token)) != receipt.Plan.ArchiveSha256)
                throw new UsageException("Frozen archive differs from authorized plan.");
        await new RestorePreparation(runner).VerifyAsync(receipt.Plan.Target, directoryPath, receipt.Plan.Operation,
            payload, archiveName, receipt.Plan.SourceInstallation, token);
        if (receipt.Phase is RestorePhase.Staging or RestorePhase.CandidateValidated)
        {
            receipt = receipt with { CandidateAttempt = checked(receipt.CandidateAttempt + 1), Phase = RestorePhase.Staging };
            receipt.Save(root);
        }
        return await ExecuteAsync(root, receipt, token);
    }
}
