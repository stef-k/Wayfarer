using System.Security.Cryptography;
using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Trusted-local forward update coordinator; uncertain mutation never becomes implicit rollback.</summary>
public sealed class UpdateCommands(IProcessRunner runner, ITerminal terminal)
{
    /// <summary>Serialize host operations, authorize exact plans, and preserve truthful phase on every failure.</summary>
    public async Task<int> RunAsync(string root, string[] args, CancellationToken token)
    {
        var options = UpdateOptions.Parse(args);
        using var operation = Setup.Lock(root);
        UninstallReceipt.RequireActive(root);
        await new Preflight(runner).DockerAsync(token);
        if (options.Plan)
        {
            using var recovery = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
            var path = options.Bundle ?? (await new PublicReleaseAcquisition(runner)
                .AcquireAsync(root, options.PublicVersion!, token)).Directory;
            var plan = await new UpdatePreparation(runner).PrepareAsync(root, path, token);
            terminal.Write(JsonSerializer.Serialize(plan));
            terminal.Write("Plan SHA-256: " + plan.Hash());
            terminal.Write("MigrationStarted ends safe old-runtime activation. Recovery requires the held archive and managed restore.");
            return 0;
        }
        UpdateReceipt receipt;
        if (options.Accept is not null)
        {
            UpdateReceipt.RequireResolved(root);
            RestoreReceipt.RequireResolved(root);
            var plan = UpdatePreparation.Load(root, options.Accept);
            RequireOwner(root, plan);
            if (File.ReadAllText(Path.Combine(root, "installation.json")) != plan.OldConfiguration ||
                File.ReadAllText(plan.Current.EnvironmentPath(root)) != plan.OldEnvironment)
                throw new UsageException("Stale update plan.");
            await new UpdatePreparation(runner).VerifyAsync(root, plan.Current, ReleaseStore.Select(root, plan.Current.Release!), token);
            receipt = new UpdateReceipt { Plan = plan, PlanHash = plan.Hash() };
            using var recovery = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
            if (File.Exists(Path.Combine(root, "recovery-control/host-operation.json"))) throw new IOException("Recovery operation active.");
            ArchivePrevious(root);
            receipt.Save(root);
        }
        else
        {
            receipt = UpdateReceipt.Load(root) ?? throw new UsageException("No update receipt.");
            if ((options.Resume ?? options.Abort ?? options.Restore) != receipt.Plan.Operation || receipt.Resolved)
                throw new UsageException("Update operation mismatch or already resolved.");
            RequireOwner(root, receipt.Plan);
        }
        try
        {
            Revalidate(root, receipt);
            if (options.Restore is not null) return await new RestoreCommands(runner, terminal).HandoffAsync(root, receipt, token);
            if (receipt.RestoreOperation is not null) throw new UsageException("Lifecycle ownership transferred; resume the receipted restore.");
            if (options.Resume is not null || options.Abort is not null)
                await ReconcileCaptureAsync(root, receipt, token);
            if (options.Abort is not null) return await AbortAsync(root, receipt, token);
            if (options.Resume is not null && receipt.Phase is >= UpdatePhase.Fenced and <= UpdatePhase.MigrationConfirmed)
            {
                using var recovery = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
                await new UpdateRuntime(runner).StopWritersAsync(root, receipt, token);
                await new UpdateRuntime(runner).StartDatabaseAsync(root, receipt, token);
            }
            return await ExecuteAsync(root, receipt, options.Resume is not null, token);
        }
        catch (Exception error)
        {
            if (error is UsageException || error is IOException && error.TargetSite?.DeclaringType?.Namespace == "WayfarerCtl")
                terminal.Error(error.Message);
            receipt = UpdateReceipt.Load(root) ?? receipt;
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            try { await new UpdateRuntime(runner).StopWritersAsync(root, receipt, cleanup.Token); }
            catch { terminal.Error("Runtime fencing uncertain; retain intent and reconcile exact Docker resources."); }
            terminal.Error($"Update {receipt.Plan.Operation:D} incomplete; phase={receipt.Phase}; writes-possible={receipt.WritesPossible}. " +
                (receipt.MigrationPossible ? "Use forward resume only after reconciliation, or explicit update --restore. Old image is not rollback." :
                    "Use resume or pre-migration abort after reconciliation."));
            return 1;
        }
    }

    /// <summary>Exact retained executable ownership survives release-pointer changes.</summary>
    public static void RequireOwner(string root, UpdatePlan plan)
    {
        var bundle = ReleaseStore.Select(root, plan.OperatorOwner);
        if (!bundle.Manifest.Operator.UpdateReceiptSchemas.Contains(1)) throw new IOException("Unsupported update receipt owner.");
        ReleaseDispatch.RequireExecutable(bundle);
    }

    private static void Revalidate(string root, UpdateReceipt receipt)
    {
        RequireOwner(root, receipt.Plan);
        var source = ReleaseStore.Select(root, receipt.Plan.Current.Release!);
        var target = ReleaseStore.Select(root, receipt.Plan.Target.Release!);
        UpdateOptions.Boundary(source, target, UpdateOptions.QualificationCandidates(root, receipt.Plan.Current.Project));
        if (!target.Manifest.Application.Migrations.SequenceEqual(receipt.Plan.TargetMigrations))
            throw new IOException("Target migration inventory changed from plan.");
        source.Corroborate(receipt.Plan.Current);
        receipt.Plan.Current.Backup!.CheckPayload();
        source.Corroborate(receipt.Plan.Current.Backup.Source);
        if (File.ReadAllText(receipt.Plan.Current.EnvironmentPath(root)) != receipt.Plan.OldEnvironment)
            throw new IOException("Retained source deployment inputs changed.");
        if (ProtectedFiles.SecretsFingerprint(root) != receipt.Plan.SecretsFingerprint)
            throw new IOException("Update credentials changed.");
        if (receipt.NewConfiguration is not null)
        {
            var activated = JsonSerializer.Deserialize<Deployment>(receipt.NewConfiguration, ArchiveContract.Json)!;
            target.Corroborate(activated);
            target.Corroborate(activated.Backup!.Source);
            activated.Backup.CheckPayload();
        }
        var current = File.ReadAllText(Path.Combine(root, "installation.json"));
        if (receipt.Phase < UpdatePhase.ActivationIntent && current != receipt.Plan.OldConfiguration ||
            current != receipt.Plan.OldConfiguration && current != receipt.NewConfiguration)
            throw new IOException("Update installation authority changed.");
    }

    /// <summary>Delegate fresh capture only while host exclusion remains owned, then regain recovery exclusion before migration.</summary>
    private async Task<int> ExecuteAsync(string root, UpdateReceipt receipt, bool resumed, CancellationToken token)
    {
        var runtime = new UpdateRuntime(runner);
        if (receipt.Phase == UpdatePhase.Authorized)
        {
            using var recovery = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
            receipt = await runtime.FenceAsync(root, receipt, token);
            receipt = receipt.Advance(UpdatePhase.Fenced);
            receipt.Save(root);
        }
        if (receipt.Phase == UpdatePhase.Fenced || resumed && receipt.Phase == UpdatePhase.RecoveryVerified)
        {
            using (var recovery = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock")))
            {
                await runtime.StopWritersAsync(root, receipt, token);
                await runtime.StartDatabaseAsync(root, receipt, token);
                await runtime.RequireExclusiveAsync(receipt, token);
                await new UpdatePreparation(runner).CapacityAsync(root, receipt.Plan.Current, token);
            }
            var capture = new BackupCommands(runner, terminal);
            if (await capture.RunAsync(root, receipt.Plan.Current, ["backup", "--quiesced"], token, updateRecovery: true) != 0 || capture.CompletedArchive is null)
                throw new IOException("Fresh verified held recovery capture failed.");
            receipt = await BindRecoveryAsync(root, UpdateReceipt.Load(root)!, capture.CompletedArchive.Value, token);
            if (receipt.Phase == UpdatePhase.Fenced) receipt = receipt.Advance(UpdatePhase.RecoveryVerified);
            receipt.Save(root);
        }
        using var exclusion = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
        if (File.Exists(Path.Combine(root, "recovery-control/host-operation.json"))) throw new IOException("Delegated capture requires reconciliation.");
        await VerifyRecoveryAsync(receipt, token);
        receipt = await runtime.MigrateAsync(root, receipt, token);
        receipt = await new UpdateActivation(runner).ActivateAsync(root, receipt, token);
        terminal.Write($"Update accepted: {receipt.Plan.Operation:D}; release={receipt.Plan.Target.Release!.Name}. Previous release/images and held recovery retained.");
        return 0;
    }

    /// <summary>Bind the exact newly captured archive and transactional hold, never an age-based substitute.</summary>
    private static async Task<UpdateReceipt> BindRecoveryAsync(string root, UpdateReceipt receipt, Guid archive, CancellationToken token)
    {
        using var destination = new SafeDirectory(receipt.Plan.Current.Backup!.Destination);
        var names = destination.Names(4096).Where(name => name.EndsWith("_" + archive.ToString("D") + ".tar", StringComparison.Ordinal)).ToArray();
        if (names.Length != 1) throw new IOException("Fresh archive publication ambiguous.");
        using var file = destination.Read(names[0]);
        receipt = receipt with { RecoveryArchive = archive, RecoveryName = names[0], RecoverySha256 = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, token)) };
        await VerifyRecoveryAsync(receipt, token);
        receipt.Save(root);
        return receipt;
    }

    /// <summary>Re-open exact bytes and hold under the configured destination inode authority before each uncertain boundary.</summary>
    public static async Task VerifyRecoveryAsync(UpdateReceipt receipt, CancellationToken token)
    {
        var policy = receipt.Plan.Current.Backup!;
        using var destination = new SafeDirectory(policy.Destination);
        var facts = destination.Identity;
        if (facts.DeviceMajor != policy.DeviceMajor || facts.DeviceMinor != policy.DeviceMinor || facts.Inode != policy.Inode ||
            receipt.RecoveryArchive is null || receipt.RecoveryName is null || receipt.RecoverySha256 is null)
            throw new IOException("Held recovery destination or archive identity changed.");
        using var hold = destination.Read(receipt.RecoveryName + ".restore-hold");
        if (hold.Length > 64 || new StreamReader(hold).ReadToEnd() != receipt.RecoveryArchive.Value.ToString("D") + "\n")
            throw new IOException("Recovery hold changed.");
        using var archive = destination.Read(receipt.RecoveryName);
        if (Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, token)) != receipt.RecoverySha256)
            throw new IOException("Held recovery bytes changed.");
    }

    /// <summary>Reconcile only this operation's exact delegated backup; interrupted capture is recaptured, never mistaken for migration.</summary>
    private async Task ReconcileCaptureAsync(string root, UpdateReceipt receipt, CancellationToken token)
    {
        var path = Path.Combine(root, "recovery-control/host-operation.json");
        var present = Path.Exists(path) || new FileInfo(path).LinkTarget is not null;
        if (receipt.MigrationPossible)
        {
            if (present) throw new IOException("Unexpected capture reservation after migration cutoff.");
            return;
        }
        var container = receipt.CaptureContainer;
        HostRecoveryOperation? reservation = null;
        string? content = null;
        if (present)
        {
            ProtectedFiles.CheckRecoveryReservation(path);
            if (new FileInfo(path).Length > 2048) throw new IOException("Invalid capture reservation.");
            content = File.ReadAllText(path);
            using var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 16 });
            var properties = new HashSet<string>(StringComparer.Ordinal);
            if (document.RootElement.EnumerateObject().Any(property => !properties.Add(property.Name)))
                throw new IOException("Duplicate capture reservation property.");
            reservation = document.RootElement.Deserialize<HostRecoveryOperation>(ArchiveContract.Json)!;
            if (receipt.Phase is not (UpdatePhase.Fenced or UpdatePhase.RecoveryVerified) || reservation.Schema != 1 ||
                !Guid.TryParseExact(reservation.Token, "N", out var delegated) || delegated == Guid.Empty ||
                reservation.Token != delegated.ToString("N") || !reservation.UpdateHold || reservation.RestoreHold ||
                !reservation.Quiesced || reservation.Container != container)
                throw new IOException("Capture reservation does not belong to this update.");
        }
        if (container is null) return;
        if (!System.Text.RegularExpressions.Regex.IsMatch(container, "\\A" +
            System.Text.RegularExpressions.Regex.Escape(receipt.Plan.Current.Project) + "-backup-[a-f0-9]{32}\\z"))
            throw new IOException("Invalid update capture identity.");
        var owner = new RestoreContainers(runner);
        var names = (await owner.Required(["ps", "-a", "--format", "{{.Names}}"], token)).Split('\n');
        if (names.Contains(container))
        {
            using var document = JsonDocument.Parse(await owner.Required(["inspect", container], token));
            VerifyCaptureContainer(root, receipt, document.RootElement[0], reservation);
            if (document.RootElement[0].GetProperty("State").GetProperty("Status").GetString() != "created")
            {
                await owner.Required(["stop", "--time", "30", container], token);
                await owner.Required(["wait", container], token);
            }
            await owner.Required(["rm", container], token);
        }
        using var recovery = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
        if (Path.Exists(path) || new FileInfo(path).LinkTarget is not null)
        {
            ProtectedFiles.CheckRecoveryReservation(path);
            if (content is null || new FileInfo(path).Length > 2048 || File.ReadAllText(path) != content)
                throw new IOException("Capture reservation changed during reconciliation.");
            File.Delete(path);
        }
        using var control = new SafeDirectory(Path.GetDirectoryName(path)!);
        control.Flush();
    }

    /// <summary>Shared read-only ownership checks let terminal uninstall inventory recognize exact receipted capture helpers.</summary>
    internal static void VerifyCaptureContainer(string root, UpdateReceipt receipt, JsonElement container, HostRecoveryOperation? reservation = null)
    {
        Preflight.VerifyRetainedResource(receipt.Plan.Current, "container", container, root);
        var configuration = container.GetProperty("Config");
        if (configuration.GetProperty("Image").GetString() != "ghcr.io/stef-k/wayfarer-db@" + receipt.Plan.Current.DbDigest)
            throw new IOException("Capture helper image changed.");
        if (configuration.GetProperty("Labels").GetProperty("com.docker.compose.service").GetString() != "backup-worker" ||
            reservation is not null && !configuration.GetProperty("Cmd").Deserialize<string[]>()!
                .SequenceEqual(new[] { "backup", "--host-operation", reservation.Token }))
            throw new IOException("Capture helper delegation changed.");
    }

    /// <summary>Abort can restore restart policies only while exact old authority remains before any migration launch.</summary>
    private async Task<int> AbortAsync(string root, UpdateReceipt receipt, CancellationToken token)
    {
        if (receipt.MigrationPossible || receipt.MigrationContainer is not null || receipt.RestoreOperation is not null)
            throw new UsageException("Migration may have started; abort is not rollback.");
        using var recovery = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
        if (File.Exists(Path.Combine(root, "recovery-control/host-operation.json"))) throw new IOException("Delegated recovery requires reconciliation.");
        var owner = new RestoreContainers(runner);
        receipt = await new UpdateRuntime(runner).FenceAsync(root, receipt, token);
        await new UpdateRuntime(runner).StartDatabaseAsync(root, receipt, token);
        await new UpdatePreparation(runner).InspectAsync(root, receipt.Plan.Current,
            ReleaseStore.Select(root, receipt.Plan.Current.Release!), receipt.Plan.Operation, token);
        foreach (var (id, policy) in receipt.RestartPolicies) await owner.Required(["update", "--restart=" + policy, id], token);
        receipt.Advance(UpdatePhase.Aborted).Save(root);
        terminal.Write("Update aborted before migration. Original restart policies restored; held recovery retained. Use start to resume service.");
        return 0;
    }

    private static void ArchivePrevious(string root)
    {
        var previous = UpdateReceipt.Load(root);
        if (previous is null) return;
        var directory = Path.Combine(root, "recovery-control", "update-history");
        Directory.CreateDirectory(directory, ProtectedFiles.PrivateDirectory);
        var path = Path.Combine(directory, previous.Plan.Operation.ToString("N") + ".json");
        if (!File.Exists(path)) ProtectedFiles.Create(path, JsonSerializer.Serialize(previous));
        using var parent = new SafeDirectory(directory);
        parent.Flush();
    }
}
