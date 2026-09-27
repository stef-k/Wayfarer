using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Ordered durable boundaries; writes-possible is irreversible even when launch acknowledgement is lost.</summary>
public enum RestorePhase
{
    Authorized, Fenced, EmergencyVerifiedOrWaived, Staging, CandidateValidated,
    ActivationIntent, ActivatedStopped, WritesPossible, Accepted, Aborted
}

/// <summary>Non-secret authorization binds the archive, independent target, generation and destructive policy.</summary>
public sealed record RestorePlan(Guid Operation, string Root, Deployment Target, Guid SourceInstallation,
    Guid Archive, string ArchiveSha256, DateTimeOffset Captured, string CaptureMode,
    string BundleFingerprint, string CapturePayloadFingerprint, string RestorePayloadFingerprint,
    string? PreviousGeneration, string CandidateGeneration, bool NewInstall, bool WithoutEmergencyBackup,
    bool ForeignAcknowledged)
{
    /// <summary>Deterministic serialized bytes bind every plan field, including the new target UUID.</summary>
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
}

/// <summary>Protected recovery intent records exact owned resources before Docker can mutate them.</summary>
public sealed record RestoreReceipt
{
    public int Schema { get; init; } = 1;
    public required RestorePlan Plan { get; init; }
    public required string PlanHash { get; init; }
    public RestorePhase Phase { get; init; }
    public string[] Containers { get; init; } = [];
    public string[] Volumes { get; init; } = [];
    public Dictionary<string, string> RestartPolicies { get; init; } = new();
    public Guid? EmergencyArchive { get; init; }
    public string? OldConfiguration { get; init; }
    public string? NewConfiguration { get; init; }

    /// <summary>Writer uncertainty is sticky; receipt recovery must never infer rollback from a failed client.</summary>
    public bool WritesPossible => Phase is RestorePhase.WritesPossible or RestorePhase.Accepted;

    public static string PathFor(string root) => Path.Combine(root, "recovery-control", "restore.json");

    /// <summary>Read only bounded root-owned intent and verify its canonical plan before using any identity.</summary>
    public static RestoreReceipt? Load(string root)
    {
        var path = PathFor(root);
        if (!File.Exists(path)) return null;
        ProtectedFiles.SafePath(path);
        ProtectedFiles.Check(path, 0);
        if (new FileInfo(path).Length > 262144) throw new UsageException("Restore receipt exceeds its bound.");
        var receipt = JsonSerializer.Deserialize<RestoreReceipt>(File.ReadAllText(path), ArchiveContract.Json)
            ?? throw new UsageException("Missing restore receipt.");
        receipt.Validate(root);
        return receipt;
    }

    /// <summary>Validate generated identities independently of any archived metadata.</summary>
    public void Validate(string root)
    {
        Plan.Target.Validate();
        if (Schema != 1 || !Enum.IsDefined(Phase) || Plan.Root != root || Plan.Operation == Guid.Empty ||
            Plan.Archive == Guid.Empty || Plan.SourceInstallation == Guid.Empty || Plan.Target.Installation == Guid.Empty ||
            PlanHash != Plan.Hash() || !System.Text.RegularExpressions.Regex.IsMatch(Plan.CandidateGeneration, "^[a-f0-9]{32}$") ||
            !System.Text.RegularExpressions.Regex.IsMatch(Plan.ArchiveSha256, "^[a-f0-9]{64}$"))
            throw new UsageException("Invalid restore receipt identity.");
    }

    /// <summary>Commit intent with file and parent-directory durability, preserving the previous checkpoint.</summary>
    public void Save(string root)
    {
        Validate(root);
        var path = PathFor(root);
        if (File.Exists(path)) ProtectedFiles.Check(path, 0);
        var temporary = path + "." + Guid.NewGuid().ToString("N");
        ProtectedFiles.Create(temporary, JsonSerializer.Serialize(this));
        File.Move(temporary, path, overwrite: true);
        using var parent = new SafeDirectory(Path.GetDirectoryName(path)!);
        parent.Flush();
    }

    /// <summary>Ordinary mutation, including backup recovery, cannot bypass an unresolved restore.</summary>
    public static void RequireResolved(string root)
    {
        var receipt = Load(root);
        if (receipt is not null && receipt.Phase is not (RestorePhase.Accepted or RestorePhase.Aborted))
            throw new UsageException($"Restore {receipt.Plan.Operation:D} is {receipt.Phase}; use restore recovery before mutation.");
    }

    /// <summary>Only forward contiguous transitions are allowed; abort is forbidden after possible writer launch.</summary>
    public RestoreReceipt Advance(RestorePhase next)
    {
        if (Phase is RestorePhase.Accepted or RestorePhase.Aborted ||
            (next == RestorePhase.Aborted ? WritesPossible : (int)next != (int)Phase + 1))
            throw new UsageException("Unsafe restore phase transition.");
        return this with { Phase = next };
    }
}

/// <summary>Central completion owner distinguishes fresh setup from accepted disaster restore.</summary>
public static class InstallationCompletion
{
    public static bool IsComplete(string root) => File.Exists(Path.Combine(root, "setup-complete")) ||
        RestoreReceipt.Load(root) is { Phase: RestorePhase.Accepted };
}
