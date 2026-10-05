using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Non-destructive planning uses the existing host lock and writes only protected plan evidence.</summary>
public sealed class UninstallPreparation(IProcessRunner runner)
{
    /// <summary>Hosted tests bind the actual retained executable fixture through the existing release-owner seam.</summary>
    internal string? ExecutablePath { get; init; }

    /// <summary>One exact lowercase hash chooses a local protected filename, never a path supplied by plan contents.</summary>
    internal static string PathFor(string root, string hash) => Path.Combine(root, "uninstall-plans", hash + ".json");

    /// <summary>Freeze configuration, terminal history and read-only Docker facts under the established host mutation lock.</summary>
    public async Task<UninstallPlan> PrepareAsync(string root, UninstallOptions options, CancellationToken token = default)
    {
        if (!options.Plan || options.Accept is not null || options.Backup is null)
            throw new UsageException("Planning requires a resolved explicit backup choice.");
        RequireRoot(root);
        using var operationLock = Setup.Lock(root);
        var state = RequireLifecycle(root);
        if (state == UninstallStartingState.Preserved && (options.Mode != UninstallMode.Purge || options.Backup != UninstallBackup.Waived))
            throw new UsageException("Already uninstalled; use start before backup, or plan purge without backup.");
        var config = Deployment.Load(root);
        var owner = ReleaseDispatch.CurrentOwner(root, config, ExecutablePath)
            ?? throw new UsageException("Uninstall requires retained release/operator authority; adopt the trusted release first.");
        CheckBackup(root, config, options.Backup.Value);
        var history = UninstallHistory.Load(root, config);
        await new Preflight(runner).DockerAsync(token);
        var inventory = await new UninstallInventory(runner).DiscoverAsync(root, config, options.Mode, history, token);
        var plan = new UninstallPlan(Guid.NewGuid(), root, config, owner, state, options.Mode, options.Backup.Value,
            Convert.ToBase64String(Read(root, "installation.json")), Convert.ToBase64String(Read(root, config.EnvironmentPath(root))),
            ProtectedFiles.SecretsFingerprint(root), BackupConfiguration.BundleFingerprint(config), history.Fingerprint(), inventory);
        plan.Validate(root);
        await RevalidateAsync(root, plan, token);
        Persist(root, plan);
        return plan;
    }

    /// <summary>Read bounded protected evidence and revalidate current protected authority before returning the plan.</summary>
    internal UninstallPlan Load(string root, string hash)
    {
        RequireRoot(root);
        if (!ReleaseContract.Hash(hash)) throw new UsageException("Invalid uninstall plan hash.");
        var path = PathFor(root, hash);
        var bytes = Read(root, path, 1048576);
        if (Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) != hash)
            throw new UsageException("Protected uninstall plan bytes changed.");
        var plan = JsonSerializer.Deserialize<UninstallPlan>(bytes, ArchiveContract.Json)
            ?? throw new UsageException("Missing uninstall plan.");
        plan.Validate(root);
        if (plan.Hash() != hash) throw new UsageException("Uninstall plan hash changed.");
        CheckProtectedAuthority(root, plan);
        return plan;
    }

    /// <summary>Public loading revalidates protected and Docker authority under the same host lock as planning.</summary>
    public async Task<UninstallPlan> LoadAsync(string root, string hash, CancellationToken token = default)
    {
        RequireRoot(root);
        using var operationLock = Setup.Lock(root);
        var plan = Load(root, hash);
        await RevalidateAsync(root, plan, token);
        return plan;
    }

    /// <summary>Caller holds Setup.Lock; exact inventory comparison is required before later destructive intent.</summary>
    public async Task RevalidateAsync(string root, UninstallPlan plan, CancellationToken token = default)
    {
        plan.Validate(root);
        var history = CheckProtectedAuthority(root, plan);
        var actual = await new UninstallInventory(runner).DiscoverAsync(root, plan.Current, plan.Mode, history, token);
        if (JsonSerializer.Serialize(actual) != JsonSerializer.Serialize(plan.Resources))
            throw new UsageException("Docker resources changed or were replaced; make a new uninstall plan.");
        if (plan.StartingState == UninstallStartingState.Preserved && actual.Any(r =>
            r.Kind is UninstallResourceKind.Container or UninstallResourceKind.Network && r.DockerId is not null))
            throw new UsageException("Normal-uninstalled receipt contradicts present runtime resources.");
        if (plan.StartingState == UninstallStartingState.Preserved)
            foreach (var retained in UninstallReceipt.Load(root)!.Plan.Resources.Where(r => r.Kind == UninstallResourceKind.Volume))
                UninstallInventory.Reconcile(retained, actual.Single(r => r.Kind == retained.Kind && r.Name == retained.Name), true);
    }

    /// <summary>Existing completion and receipt seams own refusal; derived unresolved markers cannot be bypassed.</summary>
    internal static UninstallStartingState RequireLifecycle(string root)
    {
        RequireOtherLifecycle(root);
        return UninstallReceipt.State(root) switch
        {
            UninstallState.None => UninstallStartingState.Active,
            UninstallState.Preserved => UninstallStartingState.Preserved,
            _ => throw new UsageException("Unresolved or purged uninstall authority prevents a new plan; replay its exact accepted hash.")
        };
    }

    /// <summary>Receipt-owned replay rejects unresolved lifecycle owners; retained backup identity must match committed installation authority.</summary>
    private static void RequireOtherLifecycle(string root)
    {
        UpdateReceipt.RequireResolved(root);
        RestoreReceipt.RequireResolved(root);
        if (SetupProvisioning.IsPending(root) || !InstallationCompletion.IsComplete(root))
            throw new UsageException("Incomplete/interrupted setup prevents uninstall planning.");
        foreach (var name in new[] { "backup-transition.json", "recovery-control/host-operation.json",
            "recovery-control/update-in-progress", "recovery-control/restore-in-progress" })
            if (UninstallHistory.Exists(Path.Combine(root, name)))
                throw new UsageException("Unreconciled lifecycle/backup authority prevents uninstall planning.");
        var identityPath = Path.Combine(root, "backup-identity");
        if (UninstallHistory.Exists(identityPath))
        {
            var identity = System.Text.Encoding.UTF8.GetString(Read(root, identityPath, 128));
            if (!Guid.TryParse(identity, out var installation) || installation == Guid.Empty ||
                installation != Deployment.Load(root).Installation)
                throw new UsageException("Incomplete or contradictory backup identity prevents uninstall planning.");
        }
        var completion = Path.Combine(root, "setup-complete");
        if (UninstallHistory.Exists(completion))
        {
            ProtectedFiles.SafePath(completion);
            ProtectedFiles.Check(completion, 0);
            if (!Read(root, completion, 2).SequenceEqual("1\n"u8.ToArray())) throw new UsageException("Unsafe setup completion evidence.");
        }
    }

    /// <summary>The embedded accepted plan remains protected authority during forward removal and preserved reactivation.</summary>
    internal UninstallHistory CheckReceiptAuthority(string root, UninstallReceipt receipt)
    {
        RequireRoot(root);
        receipt.Validate(root);
        var current = UninstallReceipt.Load(root) ?? throw new UsageException("Missing accepted uninstall receipt.");
        if (JsonSerializer.Serialize(current) != JsonSerializer.Serialize(receipt))
            throw new UsageException("Accepted uninstall receipt changed.");
        RequireOtherLifecycle(root);
        return CheckProtectedAuthority(root, receipt.Plan, receipt.Plan.StartingState);
    }

    /// <summary>Configuration loading already validates environment/storage/release; exact bytes additionally detect harmless-looking edits.</summary>
    private UninstallHistory CheckProtectedAuthority(string root, UninstallPlan plan, UninstallStartingState? acceptedState = null)
    {
        var state = acceptedState ?? RequireLifecycle(root);
        var config = Deployment.Load(root);
        _ = ReleaseDispatch.CurrentOwner(root, config, ExecutablePath);
        CheckBackup(root, config, plan.Backup);
        var history = UninstallHistory.Load(root, config);
        plan.CheckAuthority(config, Read(root, "installation.json"), Read(root, config.EnvironmentPath(root)),
            ProtectedFiles.SecretsFingerprint(root), BackupConfiguration.BundleFingerprint(config), history.Fingerprint(), state);
        if (state == UninstallStartingState.Preserved)
        {
            var preserved = UninstallReceipt.Load(root)!.Plan;
            preserved.CheckAuthority(config, Read(root, "installation.json"), Read(root, config.EnvironmentPath(root)),
                ProtectedFiles.SecretsFingerprint(root), BackupConfiguration.BundleFingerprint(config), history.Fingerprint(), preserved.StartingState);
        }
        var allowed = history.Volumes(config);
        var helperNetworks = history.Networks();
        foreach (var resource in plan.Resources.Where(r => r.Kind == UninstallResourceKind.Volume))
            if (!allowed.TryGetValue(resource.Name, out var owner) || owner.Role != resource.Role || owner.Operation != resource.LifecycleOperation)
                throw new UsageException("Plan has unprovable historical volume authority.");
        foreach (var resource in plan.Resources.Where(r => r.Kind == UninstallResourceKind.Network && r.LifecycleOperation is not null))
            if (!helperNetworks.TryGetValue(resource.Name, out var operation) || resource.LifecycleOperation != operation)
                throw new UsageException("Plan has unprovable historical network authority.");
        return history;
    }

    /// <summary>Backup planning never enables/configures storage; existing generated inputs must remain valid even for a waiver.</summary>
    private static void CheckBackup(string root, Deployment config, UninstallBackup backup)
    {
        if (backup == UninstallBackup.VerifiedQuiesced && config.Backup is not { Enabled: true })
            throw new UsageException("Final backup requires an enabled usable policy; choose --without-backup or cancel.");
        if (config.Backup is not null) BackupCompose.Check(root, config);
    }

    /// <summary>Publish immutable hash-addressed plan bytes and flush file, directory and installation parent.</summary>
    private static void Persist(string root, UninstallPlan plan)
    {
        var bytes = JsonSerializer.Serialize(plan);
        if (System.Text.Encoding.UTF8.GetByteCount(bytes) > 1048576) throw new UsageException("Uninstall plan exceeds bound.");
        var directory = Path.Combine(root, "uninstall-plans");
        ProtectedFiles.SafePath(directory);
        Directory.CreateDirectory(directory, ProtectedFiles.PrivateDirectory);
        ProtectedFiles.Check(directory, 0, directory: true);
        using var plans = new SafeDirectory(directory);
        plans.RequireLocalControl();
        var path = PathFor(root, plan.Hash());
        ProtectedFiles.Create(path, bytes);
        plans.Flush();
        using var installation = new SafeDirectory(root);
        installation.Flush();
    }

    /// <summary>Exact protected reads reject unsafe parents, file links, oversized inputs and unexpected roots.</summary>
    internal static byte[] Read(string root, string path, int limit = 262144)
    {
        path = Path.IsPathFullyQualified(path) ? path : Path.Combine(root, path);
        if (!path.StartsWith(root + "/", StringComparison.Ordinal)) throw new UsageException("Protected uninstall input escapes root.");
        ProtectedFiles.SafePath(path);
        ProtectedFiles.Check(path, 0);
        using var parent = new SafeDirectory(Path.GetDirectoryName(path)!);
        using var input = parent.Read(Path.GetFileName(path));
        if (input.Length > limit) throw new UsageException("Protected uninstall input exceeds bound.");
        var bytes = new byte[limit + 1];
        var count = 0;
        int read;
        while (count < bytes.Length && (read = input.Read(bytes, count, bytes.Length - count)) != 0) count += read;
        if (count > limit) throw new UsageException("Protected uninstall input grew beyond bound.");
        return bytes[..count];
    }

    /// <summary>Planning uses only canonical root-owned installation storage.</summary>
    private static void RequireRoot(string root)
    {
        BackupPolicy.LiteralPath(root);
        ProtectedFiles.RequireRoot();
        ProtectedFiles.SafePath(root);
        ProtectedFiles.Check(root, 0, directory: true);
    }
}
