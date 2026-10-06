using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Preserved means runtime was intentionally removed under a terminal normal-uninstall receipt.</summary>
public enum UninstallStartingState { Active, Preserved }

/// <summary>Images and backup destinations have an explicit exclusion, never Docker deletion authority.</summary>
public enum UninstallResourceKind { Container, Network, Volume, BackupDestination, Image }

/// <summary>Every concrete resource has exactly one frozen action.</summary>
public enum UninstallAction { Remove, Retain, Exclude }

/// <summary>Null Docker identity/evidence records proven absence; evidence contains only selected non-secret inspect facts.</summary>
public sealed record UninstallResource(UninstallResourceKind Kind, string Name, string? DockerId, string? Role,
    Guid? LifecycleOperation, UninstallAction Action, string? Evidence);

/// <summary>Concrete deterministic authority includes exact file bytes as base64 and only fingerprints of credentials.</summary>
public sealed record UninstallPlan(Guid Operation, string Root, Deployment Current, ReleaseAuthority OperatorOwner,
    UninstallStartingState StartingState, UninstallMode Mode, UninstallBackup Backup, string Configuration,
    string Environment, string SecretsFingerprint, string BundleFingerprint, string HistoryFingerprint,
    UninstallResource[] Resources)
{
    /// <summary>Version one freezes the exact inventory and backup-before-destructive-intent contract.</summary>
    public int Protocol { get; init; } = 1;

    /// <summary>Match update/restore serialization and derive the exact lowercase SHA-256 from those bytes.</summary>
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));

    /// <summary>Validate structure before a protected plan can name resources or select lifecycle behavior.</summary>
    public void Validate(string root)
    {
        BackupPolicy.LiteralPath(root);
        Current.Validate();
        OperatorOwner.Validate();
        if (Protocol != 1 || Operation == Guid.Empty || Root != root || Current.Release is null || OperatorOwner != Current.Release ||
            !Enum.IsDefined(StartingState) || !Enum.IsDefined(Mode) || !Enum.IsDefined(Backup) ||
            !ReleaseContract.Hash(SecretsFingerprint) || !ReleaseContract.Hash(BundleFingerprint) || !ReleaseContract.Hash(HistoryFingerprint))
            throw new UsageException("Invalid uninstall plan authority.");
        if (StartingState == UninstallStartingState.Preserved && (Mode != UninstallMode.Purge || Backup != UninstallBackup.Waived))
            throw new UsageException("Already uninstalled; use start before backup, or plan purge without backup.");
        if (Backup == UninstallBackup.VerifiedQuiesced && Current.Backup is not { Enabled: true })
            throw new UsageException("Final backup requires an existing enabled backup policy.");
        using var configuration = new StreamReader(new MemoryStream(Convert.FromBase64String(Configuration)), new UTF8Encoding(false, true));
        var persisted = JsonSerializer.Deserialize<Deployment>(configuration.ReadToEnd(), ArchiveContract.Json)
            ?? throw new UsageException("Missing planned installation bytes.");
        if (JsonSerializer.Serialize(persisted) != JsonSerializer.Serialize(Current))
            throw new UsageException("Planned installation bytes differ from installation identity.");
        using var environment = new StreamReader(new MemoryStream(Convert.FromBase64String(Environment)), new UTF8Encoding(false, true));
        if (environment.ReadToEnd() != Current.EnvironmentFile(root))
            throw new UsageException("Planned environment differs from installation identity.");
        ValidateResources();
    }

    /// <summary>Require exact canonical entries, defined actions and explicit exclusions independent of Docker availability.</summary>
    private void ValidateResources()
    {
        if (Resources.Length > 4096 || Resources.Select(r => (r.Kind, r.Name)).Distinct().Count() != Resources.Length)
            throw new UsageException("Ambiguous or oversized uninstall inventory.");
        foreach (var resource in Resources)
        {
            if (!Enum.IsDefined(resource.Kind) || !Enum.IsDefined(resource.Action) || resource.LifecycleOperation == Guid.Empty)
                throw new UsageException("Invalid resource action/authority.");
            if (resource.Kind is UninstallResourceKind.BackupDestination or UninstallResourceKind.Image)
            {
                if (resource.Action != UninstallAction.Exclude || resource.DockerId is not null || resource.Evidence is not null ||
                    resource.LifecycleOperation is not null || resource.Role is not null)
                    throw new UsageException("Backup destinations and images are excluded from deletion.");
                continue;
            }
            SafeDirectory.ValidateName(resource.Name);
            if (resource.Name.Contains('/') || (resource.DockerId is null) != (resource.Evidence is null))
                throw new UsageException("Invalid exact Docker resource identity.");
            if (resource.DockerId is not null && (resource.Kind == UninstallResourceKind.Volume
                ? resource.DockerId != resource.Name : !ReleaseContract.Hash(resource.DockerId)))
                throw new UsageException("Invalid exact Docker ID.");
            var action = resource.Kind == UninstallResourceKind.Volume && Mode == UninstallMode.Normal ? UninstallAction.Retain : UninstallAction.Remove;
            if (resource.Action != action) throw new UsageException("Resource action differs from uninstall mode.");
            if (resource.Evidence is not null)
            {
                using var facts = JsonDocument.Parse(resource.Evidence);
                if (facts.RootElement.ValueKind != JsonValueKind.Object) throw new UsageException("Invalid resource evidence.");
                if (facts.RootElement.GetProperty("Name").GetString()!.TrimStart('/') != resource.Name ||
                    resource.Kind != UninstallResourceKind.Volume && facts.RootElement.GetProperty("Id").GetString() != resource.DockerId)
                    throw new UsageException("Resource evidence differs from planned identity.");
            }
        }
        foreach (var service in UninstallInventory.Services(Current))
            Require(UninstallResourceKind.Container, Current.Project + "-" + service + "-1");
        foreach (var network in new[] { "backend", "edge" }) Require(UninstallResourceKind.Network, Current.Project + "_" + network);
        foreach (var role in UninstallInventory.Roles(Current)) Require(UninstallResourceKind.Volume, ActiveStorage.Volume(Current, role));
        if (Mode == UninstallMode.Normal && new[] { "db-data", "app-data" }.Any(role =>
            Require(UninstallResourceKind.Volume, ActiveStorage.Volume(Current, role)).DockerId is null))
            throw new UsageException("Authoritative DB/application volumes are missing; installation cannot be preserved.");
        var excluded = Resources.Where(r => r.Kind is UninstallResourceKind.Image or UninstallResourceKind.BackupDestination)
            .Select(r => (r.Kind, r.Name)).OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.Ordinal);
        var expected = UninstallInventory.Exclusions(Current).Select(r => (r.Kind, r.Name))
            .OrderBy(r => r.Kind).ThenBy(r => r.Name, StringComparer.Ordinal);
        if (!excluded.SequenceEqual(expected)) throw new UsageException("Uninstall exclusions differ from installation authority.");
    }

    /// <summary>Missing canonical inventory entries are a malformed plan, rather than implicit absence.</summary>
    private UninstallResource Require(UninstallResourceKind kind, string name) => Resources.SingleOrDefault(r => r.Kind == kind && r.Name == name)
        ?? throw new UsageException("Incomplete uninstall inventory.");

    /// <summary>Exact protected bytes and independently selected release/secrets/history must still match at acceptance.</summary>
    internal void CheckAuthority(Deployment current, byte[] configuration, byte[] environment, string secrets,
        string bundle, string history, UninstallStartingState state)
    {
        if (JsonSerializer.Serialize(Current) != JsonSerializer.Serialize(current) || OperatorOwner != current.Release ||
            Configuration != Convert.ToBase64String(configuration) || Environment != Convert.ToBase64String(environment) ||
            SecretsFingerprint != secrets || BundleFingerprint != bundle || HistoryFingerprint != history || StartingState != state)
            throw new UsageException("Uninstall plan is stale; installation/environment/secrets/release/history authority changed.");
    }
}
