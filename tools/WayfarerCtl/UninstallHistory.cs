using System.Security.Cryptography;
using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Terminal protected lifecycle receipts prove historical storage; names or project labels alone never do.</summary>
internal sealed record UninstallHistory(UpdateReceipt[] Updates, RestoreReceipt[] Restores)
{
    /// <summary>Bound current and retained receipt bytes without retaining additional copies in the uninstall plan.</summary>
    internal string Fingerprint() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));

    /// <summary>Read every bounded history entry and refuse unresolved, malformed, foreign or contradictory authority.</summary>
    internal static UninstallHistory Load(string root, Deployment config)
    {
        var updates = Read<UpdateReceipt>(root, "update", UpdateReceipt.Load(root));
        var restores = Read<RestoreReceipt>(root, "restore", RestoreReceipt.Load(root));
        var history = new UninstallHistory(updates, restores);
        history.Validate(root, config);
        var completion = Path.Combine(root, "restore-complete");
        if (Exists(completion))
        {
            var bytes = System.Text.Encoding.UTF8.GetString(UninstallPreparation.Read(root, completion, 36));
            if (!Guid.TryParseExact(bytes, "D", out var operation) || operation == Guid.Empty ||
                !restores.Any(receipt => receipt.Plan.Operation == operation && receipt.Phase == RestorePhase.Accepted))
                throw new UsageException("Restore completion lacks accepted protected history.");
        }
        return history;
    }

    /// <summary>Pure receipt validation is shared with inventory tests; only terminal same-installation authority qualifies.</summary>
    internal void Validate(string root, Deployment config)
    {
        foreach (var update in Updates)
        {
            update.Validate(root);
            if (!update.Resolved) throw new UsageException("Unresolved update prevents uninstall planning.");
            SameInstallation(config, update.Plan.Current);
            SameInstallation(config, update.Plan.Target);
        }
        foreach (var restore in Restores)
        {
            restore.Validate(root);
            if (restore.Phase is not (RestorePhase.Accepted or RestorePhase.Aborted))
                throw new UsageException("Unresolved restore prevents uninstall planning.");
            SameInstallation(config, restore.Plan.Target);
            if (restore.Plan.PreviousGeneration != restore.Plan.Target.StorageGeneration)
                throw new UsageException("Restore previous storage authority differs from target.");
            if (restore.Plan.FromUpdate is not null) UpdateRestoreHandoff.Require(root, restore.Plan);
        }
        _ = Volumes(config);
    }

    /// <summary>History selection is generated locally, no-follow and bounded; current/history duplicates must agree exactly.</summary>
    private static T[] Read<T>(string root, string kind, T? current) where T : class
    {
        var found = new SortedDictionary<string, T>(StringComparer.Ordinal);
        Guid Operation(T value) => value is UpdateReceipt update ? update.Plan.Operation : ((RestoreReceipt)(object)value).Plan.Operation;
        if (current is not null) found.Add(Operation(current).ToString("N"), current);
        var directory = Path.Combine(root, "recovery-control", kind + "-history");
        if (!Exists(directory)) return found.Values.ToArray();
        ProtectedFiles.SafePath(directory);
        ProtectedFiles.Check(directory, 0, directory: true);
        using var parent = new SafeDirectory(directory);
        foreach (var name in parent.Names(100))
        {
            if (!name.EndsWith(".json", StringComparison.Ordinal) || !Guid.TryParseExact(name[..^5], "N", out var operation) || operation == Guid.Empty)
                throw new UsageException("Unexpected lifecycle history entry.");
            var path = Path.Combine(directory, name);
            ProtectedFiles.Check(path, 0);
            using var input = parent.Read(name);
            if (input.Length > 1048576) throw new UsageException("Lifecycle history exceeds bound.");
            var value = JsonSerializer.Deserialize<T>(input, ArchiveContract.Json) ?? throw new UsageException("Missing lifecycle history.");
            if (Operation(value) != operation) throw new UsageException("Lifecycle history operation differs from filename.");
            var key = operation.ToString("N");
            if (found.TryGetValue(key, out var previous) && JsonSerializer.Serialize(previous) != JsonSerializer.Serialize(value))
                throw new UsageException("Contradictory current and historical lifecycle receipt.");
            found[key] = value;
        }
        return found.Values.ToArray();
    }

    /// <summary>Concrete role/configuration pairs retain the existing ActiveStorage and Preflight naming/label authority.</summary>
    internal Dictionary<string, (Deployment Config, string Role, Guid? Operation)> Volumes(Deployment current)
    {
        var volumes = new Dictionary<string, (Deployment, string, Guid?)>(StringComparer.Ordinal);
        void Add(Deployment config, Guid? operation)
        {
            config.Validate();
            if (config.Project != current.Project) throw new UsageException("Foreign historical project.");
            foreach (var role in UninstallInventory.Roles(config)) volumes.TryAdd(ActiveStorage.Volume(config, role), (config, role, operation));
        }
        Add(current, null);
        foreach (var update in Updates)
        {
            Add(update.Plan.Current, update.Plan.Operation);
            Add(update.Plan.Target, update.Plan.Operation);
            var old = Configuration(update.Plan.OldConfiguration);
            if (JsonSerializer.Serialize(old) != JsonSerializer.Serialize(update.Plan.Current)) throw new UsageException("Changed historical update configuration.");
            if (update.NewConfiguration is not null) Add(Configuration(update.NewConfiguration), update.Plan.Operation);
        }
        foreach (var restore in Restores)
        {
            Add(restore.Plan.Target, restore.Plan.Operation);
            if (restore.OldConfiguration is not null)
            {
                var old = Configuration(restore.OldConfiguration);
                var joined = restore.Plan.FromUpdate is { } operation ? Updates.SingleOrDefault(update => update.Plan.Operation == operation) : null;
                var fromUpdate = joined is not null && new[] { joined.Plan.OldConfiguration, joined.NewConfiguration }
                    .Any(bytes => bytes is not null && JsonSerializer.Serialize(Configuration(bytes)) == JsonSerializer.Serialize(old));
                if (!fromUpdate && JsonSerializer.Serialize(old with { Installation = restore.Plan.Target.Installation, Schema = restore.Plan.Target.Schema }) !=
                    JsonSerializer.Serialize(restore.Plan.Target)) throw new UsageException("Changed historical restore configuration.");
                Add(old, restore.Plan.Operation);
            }
            if (restore.NewConfiguration is not null)
            {
                var next = Configuration(restore.NewConfiguration);
                if (JsonSerializer.Serialize(next) != JsonSerializer.Serialize(RestoreCandidate.Configuration(restore.EffectivePlan)))
                    throw new UsageException("Changed historical restore candidate configuration.");
                Add(next, restore.Plan.Operation);
            }
            var candidates = Enumerable.Range(0, restore.CandidateAttempt + 1).Select(attempt =>
                RestoreCandidate.Configuration((restore with { CandidateAttempt = attempt }).EffectivePlan)).ToArray();
            foreach (var name in restore.Volumes)
            {
                var candidate = candidates.FirstOrDefault(config => UninstallInventory.Roles(config).Any(role => ActiveStorage.Volume(config, role) == name))
                    ?? throw new UsageException("Unreceipted restore volume generation.");
                var role = UninstallInventory.Roles(candidate).Single(role => ActiveStorage.Volume(candidate, role) == name);
                volumes.TryAdd(name, (candidate, role, restore.Plan.Operation));
            }
        }
        return volumes;
    }

    /// <summary>Only generations recorded in terminal restore volume intent can authorize helper networks.</summary>
    internal Dictionary<string, Guid> Networks() => Restores.SelectMany(receipt => Enumerable.Range(0, receipt.CandidateAttempt + 1)
        .Select(attempt => (receipt with { CandidateAttempt = attempt }).EffectivePlan)
        .Where(plan => receipt.Volumes.Contains(ActiveStorage.Volume(RestoreCandidate.Configuration(plan), "db-data")))
        .Select(plan => (Name: RestoreCandidate.Network(plan), receipt.Plan.Operation)))
        .ToDictionary(value => value.Name, value => value.Operation, StringComparer.Ordinal);

    /// <summary>A helper needs exact receipt membership plus the existing image/mount validator and terminal stopped state.</summary>
    internal Guid Helper(string root, Deployment config, JsonElement container)
    {
        var name = container.GetProperty("Name").GetString()!.TrimStart('/');
        var capture = Updates.SingleOrDefault(receipt => receipt.CaptureContainer == name);
        if (capture is not null && !container.GetProperty("State").GetProperty("Running").GetBoolean() &&
            container.GetProperty("HostConfig").GetProperty("RestartPolicy").GetProperty("Name").GetString() == "no")
        {
            UpdateCommands.VerifyCaptureContainer(root, capture, container);
            UninstallInventory.VerifyRecoveryService(root, capture.Plan.Current, container, false);
            return capture.Plan.Operation;
        }
        if (!RestoreFencing.IsRetainedHelper(root, config, container)) throw new UsageException("Foreign or unreceipted lifecycle helper.");
        var id = container.GetProperty("Id").GetString()!;
        var restore = Restores.FirstOrDefault(r => r.Containers.Contains(name) || r.Containers.Contains(id));
        if (restore is not null) return restore.Plan.Operation;
        var update = Updates.FirstOrDefault(r => r.MigrationContainerId == id && r.MigrationContainer == name);
        return update?.Plan.Operation ?? throw new UsageException("Helper has no terminal installation-owned receipt.");
    }

    /// <summary>Parse only protected non-secret configuration snapshots using the existing schema validator.</summary>
    private static Deployment Configuration(string bytes)
    {
        var config = JsonSerializer.Deserialize<Deployment>(bytes, ArchiveContract.Json) ?? throw new UsageException("Missing historical configuration.");
        config.Validate();
        return config;
    }

    /// <summary>A receipt for a different installation/project is never historical deletion authority.</summary>
    private static void SameInstallation(Deployment current, Deployment previous)
    {
        if (current.Project != previous.Project || current.Installation != previous.Installation)
            throw new UsageException("Foreign lifecycle history cannot authorize uninstall resources.");
    }

    /// <summary>Dangling links and wrong-type entries must be handled as existing unsafe state.</summary>
    internal static bool Exists(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget is not null;
}
