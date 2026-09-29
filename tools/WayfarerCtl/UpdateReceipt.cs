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
    string OldConfiguration, string OldEnvironment, string SecretsFingerprint, UpdateCapacity Capacity)
{
    public int UpdateProtocol { get; init; } = 1;
    public string RecoveryRequirement { get; init; } = "fresh-verified-quiesced-held";
    public string Fencing { get; init; } = "stop-app-jobs-ingress-scheduler;restart=no";
    public string Postflight { get; init; } = "exact-EF-Quartz-DB-DP-Identity-credentials-Uploads;private-before-ingress";
    public string[] TargetMigrations { get; init; } = [];
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
}

/// <summary>Bounded observed capacity evidence, including retained-data overhead and operating reserve.</summary>
public sealed record UpdateCapacity(long RequiredBytes, long RootAvailableBytes, long DestinationAvailableBytes,
    long DockerAvailableBytes, long DatabaseBytes, long ApplicationBytes);

/// <summary>Protected forward intent retains resource, recovery and activation evidence until explicit acceptance.</summary>
public sealed record UpdateReceipt
{
    public int Schema { get; init; } = 1;
    public required UpdatePlan Plan { get; init; }
    public required string PlanHash { get; init; }
    public UpdatePhase Phase { get; init; }
    public Dictionary<string, string> RestartPolicies { get; init; } = new();
    public Dictionary<string, string> ServiceRestartPolicies { get; init; } = new();
    public string[] Containers { get; init; } = [];
    public string? CaptureContainer { get; init; }
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

    /// <summary>Resolve retained operation evidence by UUID; history remains authority after a later update begins.</summary>
    public static UpdateReceipt? Find(string root, Guid operation)
    {
        var current = Load(root);
        if (current?.Plan.Operation == operation) return current;
        var path = Path.Combine(root, "recovery-control", "update-history", operation.ToString("N") + ".json");
        if (!File.Exists(path)) return null;
        ProtectedFiles.SafePath(path);
        ProtectedFiles.Check(path, 0);
        if (new FileInfo(path).Length > 1048576) throw new IOException("Retained update receipt exceeds bound.");
        var receipt = JsonSerializer.Deserialize<UpdateReceipt>(File.ReadAllText(path), ArchiveContract.Json)
            ?? throw new IOException("Missing retained update receipt.");
        receipt.Validate(root);
        if (receipt.Plan.Operation != operation) throw new IOException("Retained update operation mismatch.");
        return receipt;
    }

    /// <summary>Recompute authorization before any names or release selectors acquire authority.</summary>
    public void Validate(string root)
    {
        Plan.Current.Validate();
        Plan.Target.Validate();
        Plan.OperatorOwner.Validate();
        if (Plan.UpdateProtocol != 1 || Plan.RecoveryRequirement != "fresh-verified-quiesced-held" ||
            Plan.Fencing != "stop-app-jobs-ingress-scheduler;restart=no" ||
            Plan.Postflight != "exact-EF-Quartz-DB-DP-Identity-credentials-Uploads;private-before-ingress" ||
            Plan.Current.Backup is null ||
            !Plan.Current.Backup.Source.ExpectedMigrations.Concat(Plan.MigrationDelta).SequenceEqual(Plan.TargetMigrations))
            throw new IOException("Unsupported update plan contract.");
        if (Schema != 1 || !Enum.IsDefined(Phase) || Plan.Root != root || Plan.Operation == Guid.Empty ||
            PlanHash != Plan.Hash() || Plan.Current.Release is null || Plan.Target.Release is null ||
            Plan.Current.Installation == Guid.Empty || Plan.Current.Installation != Plan.Target.Installation ||
            Plan.Current.Project != Plan.Target.Project || Plan.Current.StorageGeneration != Plan.Target.StorageGeneration)
            throw new IOException("Invalid update receipt authority.");
        if (Phase >= UpdatePhase.RecoveryVerified && Phase != UpdatePhase.Aborted &&
            (RecoveryArchive is null || RecoveryArchive == Guid.Empty || RecoveryName is null || !ReleaseContract.Hash(RecoverySha256 ?? "")))
            throw new IOException("Update phase lacks held recovery evidence.");
        if (MigrationPossible && MigrationContainer != Plan.Current.Project + "-update-migrate-" + Plan.Operation.ToString("N"))
            throw new IOException("Update phase lacks exact migration helper intent.");
        if (Phase != UpdatePhase.Aborted && (Phase >= UpdatePhase.ActivationIntent) != (NewConfiguration is not null))
            throw new IOException("Update activation phase/configuration mismatch.");
        if (NewConfiguration is not null)
        {
            var next = JsonSerializer.Deserialize<Deployment>(NewConfiguration, ArchiveContract.Json)
                ?? throw new IOException("Missing target configuration.");
            next.Validate();
            if (JsonSerializer.Serialize(next with { Backup = Plan.Target.Backup }) != JsonSerializer.Serialize(Plan.Target))
                throw new IOException("Target configuration exceeds update authorization.");
            var backup = next.Backup ?? throw new IOException("Target recovery binding missing.");
            var previous = Plan.Target.Backup!;
            if (backup.Generation != PlanHash || backup.Payload != Path.Combine(Plan.Target.Bundle, "wayfarer-recovery") ||
                JsonSerializer.Serialize(backup with { Generation = previous.Generation, Payload = previous.Payload,
                    PayloadSha256 = previous.PayloadSha256, Source = previous.Source }) != JsonSerializer.Serialize(previous))
                throw new IOException("Target backup policy exceeds update authorization.");
        }
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
