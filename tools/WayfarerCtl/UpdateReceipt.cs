using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>MigrationStarted is the irreversible old-runtime cutoff, including lost launch acknowledgement.</summary>
public enum UpdatePhase
{
    Authorized, Fenced, RecoveryVerified, MigrationStarted, MigrationConfirmed, ActivationIntent,
    TargetActivatedStopped, WritesPossible, PostflightConfirmed, Accepted, Aborted
}

/// <summary>Canonical forward authorization binds independently retained releases and the unchanged physical generation.</summary>
public sealed record UpdatePlan(Guid Operation, string Root, Deployment Current, Deployment Target,
    ReleaseAuthority OperatorOwner, ReleaseSourceBoundary Boundary, string[] MigrationDelta,
    string OldConfiguration, string OldEnvironment, string SecretsFingerprint, long RequiredCapacity)
{
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
}

/// <summary>Protected forward intent retains resource, recovery and activation evidence until explicit acceptance.</summary>
public sealed record UpdateReceipt
{
    public int Schema { get; init; } = 1;
    public required UpdatePlan Plan { get; init; }
    public required string PlanHash { get; init; }
    public UpdatePhase Phase { get; init; }
    public Dictionary<string, string> RestartPolicies { get; init; } = new();
    public string[] Containers { get; init; } = [];
    public Guid? RecoveryArchive { get; init; }
    public string? RecoveryName { get; init; }
    public string? RecoverySha256 { get; init; }
    public string? MigrationContainer { get; init; }
    public string? MigrationContainerId { get; init; }
    public int? MigrationExit { get; init; }
    public string? Reconciliation { get; init; }
    public string? NewConfiguration { get; init; }
    public Guid? RestoreOperation { get; init; }
    public bool MigrationPossible => Phase >= UpdatePhase.MigrationStarted && Phase != UpdatePhase.Aborted;
    public bool WritesPossible => Phase >= UpdatePhase.WritesPossible && Phase != UpdatePhase.Aborted;
    public bool RestoreAccepted { get; init; }
    public bool Resolved => Phase is UpdatePhase.Accepted or UpdatePhase.Aborted || RestoreAccepted;
    public static string PathFor(string root) => Path.Combine(root, "recovery-control", "update.json");

    /// <summary>Only contiguous forward steps or pre-migration abort may change the cutoff.</summary>
    public UpdateReceipt Advance(UpdatePhase next)
    {
        if (Resolved || (next == UpdatePhase.Aborted ? MigrationPossible : (int)next != (int)Phase + 1))
            throw new UsageException("Unsafe update transition; migration requires forward reconciliation or explicit restore.");
        return this with { Phase = next };
    }

    /// <summary>Strict bounded loading is usable before ordinary deployment loading during pointer transitions.</summary>
    public static UpdateReceipt? Load(string root)
    {
        var path = PathFor(root);
        if (!File.Exists(path)) return null;
        ProtectedFiles.SafePath(path);
        ProtectedFiles.Check(path, 0);
        if (new FileInfo(path).Length > 1048576) throw new IOException("Update receipt exceeds bound.");
        var receipt = JsonSerializer.Deserialize<UpdateReceipt>(File.ReadAllText(path), ArchiveContract.Json)
            ?? throw new IOException("Missing update receipt.");
        receipt.Validate(root);
        return receipt;
    }

    /// <summary>Recompute authorization before any names or release selectors acquire authority.</summary>
    public void Validate(string root)
    {
        Plan.Current.Validate();
        Plan.Target.Validate();
        Plan.OperatorOwner.Validate();
        if (Schema != 1 || !Enum.IsDefined(Phase) || Plan.Root != root || Plan.Operation == Guid.Empty ||
            PlanHash != Plan.Hash() || Plan.Current.Release is null || Plan.Target.Release is null ||
            Plan.Current.Installation == Guid.Empty || Plan.Current.Installation != Plan.Target.Installation ||
            Plan.Current.Project != Plan.Target.Project || Plan.Current.StorageGeneration != Plan.Target.StorageGeneration)
            throw new IOException("Invalid update receipt authority.");
    }

    /// <summary>Flush intent before mutation; an unresolved marker also excludes independent recovery workers.</summary>
    public void Save(string root)
    {
        Validate(root);
        var path = PathFor(root);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        ProtectedFiles.Create(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, true);
        var marker = Path.Combine(root, "recovery-control", "update-in-progress");
        if (Resolved)
        {
            if (File.Exists(marker)) File.Delete(marker);
        }
        else if (!File.Exists(marker)) ProtectedFiles.Create(marker, Plan.Operation.ToString("D"));
        using var parent = new SafeDirectory(Path.GetDirectoryName(path)!);
        parent.Flush();
    }

    /// <summary>A transferred update stays unresolved until its exact restore accepts; intent is never deleted.</summary>
    public static void RequireResolved(string root)
    {
        if (Load(root) is { Resolved: false } receipt)
            throw new UsageException($"Update {receipt.Plan.Operation:D} is {receipt.Phase}; use update recovery before mutation.");
    }
}
