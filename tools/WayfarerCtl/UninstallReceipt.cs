using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Durable semantic cutoffs branch only after runtime removal; no rollback exists after authorization.</summary>
public enum UninstallPhase { Authorized, Fenced, RuntimeRemoved, Preserved, VolumesRemoved, Purged }

/// <summary>Ordinary lifecycle can later distinguish intentional preservation from unresolved removal without overloading setup markers.</summary>
public enum UninstallState { None, Unresolved, Preserved, Purged }

/// <summary>The exact committed archive and explicit successful verifier result are retained once, never recaptured on replay.</summary>
public sealed record UninstallBackupEvidence(Guid Archive, string Basename, string Sha256, DateTimeOffset Captured,
    bool IntegrityValid, bool CompatibilitySupported);

/// <summary>Protected forward intent binds the accepted plan plus execution evidence without secret bytes.</summary>
public sealed record UninstallReceipt
{
    /// <summary>Version one uses the exact planning and forward-only phase protocol.</summary>
    public int Schema { get; init; } = 1;
    /// <summary>The complete accepted inventory remains authority until the later purge tombstone commit.</summary>
    public required UninstallPlan Plan { get; init; }
    /// <summary>Exact accepted lowercase SHA-256 is the sole replay authorization.</summary>
    public required string PlanHash { get; init; }
    /// <summary>Last durable semantic checkpoint.</summary>
    public UninstallPhase Phase { get; init; }
    /// <summary>Verified final capture, absent only for an explicit waiver.</summary>
    public UninstallBackupEvidence? FinalBackup { get; init; }
    /// <summary>Observed absence after exact ID reconciliation, retained for lost acknowledgements.</summary>
    public string[] RemovedContainers { get; init; } = [];
    /// <summary>Observed absence of exact planned network IDs.</summary>
    public string[] RemovedNetworks { get; init; } = [];
    /// <summary>Purge-only observed absence of exact planned volume names.</summary>
    public string[] RemovedVolumes { get; init; } = [];
    /// <summary>Normal terminal authority remains complete; purge terminal replacement belongs to Handoff 3.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Terminal => Phase is UninstallPhase.Preserved or UninstallPhase.Purged;

    /// <summary>Receipt location is installation-owned even when backup has never provisioned recovery-control.</summary>
    public static string PathFor(string root) => Path.Combine(root, "uninstall.json");

    /// <summary>Only mode-valid contiguous transitions can advance destructive authority.</summary>
    public UninstallReceipt Advance(UninstallPhase next)
    {
        var expected = Phase switch
        {
            UninstallPhase.Authorized => UninstallPhase.Fenced,
            UninstallPhase.Fenced => UninstallPhase.RuntimeRemoved,
            UninstallPhase.RuntimeRemoved => Plan.Mode == UninstallMode.Normal ? UninstallPhase.Preserved : UninstallPhase.VolumesRemoved,
            UninstallPhase.VolumesRemoved when Plan.Mode == UninstallMode.Purge => UninstallPhase.Purged,
            _ => throw new UsageException("Terminal uninstall authority cannot transition backward.")
        };
        if (next != expected) throw new UsageException("Unsafe uninstall phase transition.");
        return this with { Phase = next };
    }

    /// <summary>Validate the exact plan/hash, mode/phase, final-backup proof and bounded resource reconciliation evidence.</summary>
    public void Validate(string root)
    {
        Plan.Validate(root);
        if (Schema != 1 || !Enum.IsDefined(Phase) || !ReleaseContract.Hash(PlanHash) || PlanHash != Plan.Hash() ||
            Plan.Mode == UninstallMode.Normal && Phase is UninstallPhase.VolumesRemoved or UninstallPhase.Purged ||
            Plan.Mode == UninstallMode.Purge && Phase == UninstallPhase.Preserved)
            throw new UsageException("Invalid uninstall receipt authority or phase.");
        if (Plan.Backup == UninstallBackup.Waived ? FinalBackup is not null : FinalBackup is null)
            throw new UsageException("Uninstall backup evidence differs from the accepted choice.");
        if (FinalBackup is { } backup && (backup.Archive == Guid.Empty || !ReleaseContract.Hash(backup.Sha256) ||
            !backup.IntegrityValid || !backup.CompatibilitySupported ||
            backup.Basename != ArchiveContract.Name(Plan.Current.Installation, backup.Captured, backup.Archive)))
            throw new UsageException("Uninstall lacks exact verified final-backup evidence.");
        ValidateRemoved(UninstallResourceKind.Container, RemovedContainers);
        ValidateRemoved(UninstallResourceKind.Network, RemovedNetworks);
        ValidateRemoved(UninstallResourceKind.Volume, RemovedVolumes);
        if (Plan.Mode == UninstallMode.Normal && RemovedVolumes.Length != 0)
            throw new UsageException("Normal uninstall cannot delete volumes.");
    }

    /// <summary>Receipt evidence cannot silently add removal targets outside the accepted exact inventory.</summary>
    private void ValidateRemoved(UninstallResourceKind kind, string[] removed)
    {
        var allowed = Plan.Resources.Where(r => r.Kind == kind && r.Action == UninstallAction.Remove && r.DockerId is not null)
            .Select(r => r.DockerId!).ToArray();
        if (removed.Distinct(StringComparer.Ordinal).Count() != removed.Length || removed.Except(allowed, StringComparer.Ordinal).Any())
            throw new UsageException("Uninstall reconciliation exceeds accepted resource authority.");
        var complete = kind == UninstallResourceKind.Volume ? Phase >= UninstallPhase.VolumesRemoved : Phase >= UninstallPhase.RuntimeRemoved;
        if (removed.Length != 0 && Phase < (kind == UninstallResourceKind.Volume ? UninstallPhase.RuntimeRemoved : UninstallPhase.Fenced))
            throw new UsageException("Removal evidence precedes its durable destructive cutoff.");
        if (complete && allowed.Except(removed, StringComparer.Ordinal).Any())
            throw new UsageException("Uninstall checkpoint lacks complete resource absence evidence.");
    }

    /// <summary>No absence marker, malformed receipt or unsafe file grants uninstall terminal authority.</summary>
    public static UninstallReceipt? Load(string root)
    {
        var path = PathFor(root);
        if (!UninstallHistory.Exists(path)) return null;
        var receipt = JsonSerializer.Deserialize<UninstallReceipt>(UninstallPreparation.Read(root, path, 2097152), ArchiveContract.Json)
            ?? throw new UsageException("Missing uninstall receipt.");
        receipt.Validate(root);
        return receipt;
    }

    /// <summary>Explicit query leaves status/start/setup behavior unchanged until their execution handoffs.</summary>
    public static UninstallState State(string root) => Load(root) switch
    {
        null => UninstallState.None,
        { Phase: UninstallPhase.Preserved } => UninstallState.Preserved,
        { Phase: UninstallPhase.Purged } => UninstallState.Purged,
        _ => UninstallState.Unresolved
    };

    /// <summary>Caller holds the host lock; flush replacement before a later destructive cutoff, never superseding another plan.</summary>
    public void Save(string root)
    {
        Validate(root);
        var previous = Load(root);
        if (previous is not null)
        {
            if (previous.PlanHash != PlanHash || previous.FinalBackup != FinalBackup ||
                previous.Phase != Phase && previous.Advance(Phase).Phase != Phase ||
                previous.RemovedContainers.Except(RemovedContainers).Any() || previous.RemovedNetworks.Except(RemovedNetworks).Any() ||
                previous.RemovedVolumes.Except(RemovedVolumes).Any())
                throw new UsageException("Uninstall receipt cannot supersede or regress durable authority.");
        }
        else if (Phase != UninstallPhase.Authorized) throw new UsageException("New uninstall intent must begin Authorized.");
        var bytes = JsonSerializer.Serialize(this);
        if (System.Text.Encoding.UTF8.GetByteCount(bytes) > 2097152) throw new UsageException("Uninstall receipt exceeds bound.");
        ProtectedFiles.SafePath(root);
        ProtectedFiles.Check(root, 0, directory: true);
        using var parent = new SafeDirectory(root);
        parent.RequireLocalControl();
        var path = PathFor(root);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        ProtectedFiles.Create(temporary, bytes);
        File.Move(temporary, path, true);
        parent.Flush();
    }
}
