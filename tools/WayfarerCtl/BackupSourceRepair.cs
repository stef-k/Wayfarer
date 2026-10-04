using System.Security.Cryptography;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Explicit metadata migration for immutable public v1.9.21; grants no ordinary foreign-operator authority.</summary>
public sealed class BackupSourceRepair(IProcessRunner runner)
{
    /// <summary>Public release fingerprints include the exact manifest, inventory and retained operator bytes.</summary>
    internal static string PublicFingerprint(string platform) => platform switch
    {
        "linux/amd64" => "670dae009435370e2c02f1aaa74f6c93eb77c30290350ba92df4c5c3af1f623c",
        "linux/arm64" => "88d5b35c359cec3f257026fed52cc705d0642dc116e5d712824b1aae89a3276e",
        _ => throw new IOException("No public v1.9.21 repair authority for this platform.")
    };

    /// <summary>Select protected current authority under host exclusion before entering the one supported repair.</summary>
    public async Task<bool> RunAsync(string root, CancellationToken token)
    {
        ProtectedFiles.RequireRoot();
        ProtectedFiles.SafePath(Environment.ProcessPath ?? throw new IOException("Repair operator path unavailable."));
        ProtectedFiles.SafePath(root);
        ProtectedFiles.Check(root, 0, directory: true);
        using var operation = Setup.Lock(root);
        RequireIdle(root);
        var config = Deployment.Load(root);
        var bundle = ReleaseStore.Select(root, config.Release ?? throw new IOException("Repair requires retained public release authority."));
        return await RepairAsync(root, config, bundle, token);
    }

    /// <summary>Already validated release bytes are the sole source; publish all derived inputs through the existing generation transaction.</summary>
    internal async Task<bool> RepairAsync(string root, Deployment config, ReleaseBundle bundle, CancellationToken token)
    {
        RequireIdle(root);
        RequirePublicRelease(bundle);
        config.Validate();
        bundle.Corroborate(config);
        var policy = config.Backup ?? throw new IOException("Repair requires a configured backup.");
        var target = bundle.Target(config.Project, currentCapture: true);
        if (config.Schema != 4 || config.Installation == Guid.Empty || config.Release != ReleaseAuthority.From(bundle) ||
            policy.Source.ConfigurationSchema != 3 || policy.Source.Project != config.Project ||
            policy.PayloadSha256 != target.PayloadFingerprint || policy.Source.PayloadFingerprint != target.PayloadFingerprint ||
            policy.Uploads != bundle.Manifest.Application.Uploads || policy.Ring != bundle.Manifest.Application.Ring ||
            !policy.Source.SupportedLegacySourceSchemas!.SequenceEqual(target.SupportedLegacySourceSchemas!))
            throw new IOException("Backup is outside the exact public v1.9.21 current-capture repair.");
        var corrected = policy.Source with { ReleaseStatus = "released" };
        // Exact ordinary corroboration still rejects every other contradictory source fact.
        bundle.Corroborate(corrected);
        ProtectedFiles.SafePath(Path.Combine(root, "recovery-control"));
        using var recovery = new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
        RequireIdle(root);
        BackupCompose.Check(root, config);
        RequireDestination(policy, config.Installation);
        if (policy.Source.ReleaseStatus == "released") return false;
        await new Preflight(runner).DockerAsync(token);
        var scheduler = await runner.RunAsync(BackupCompose.Command(root, config, "ps", "-aq", "backup-scheduler"), null, token);
        if (scheduler.Code != 0) throw new IOException("Backup scheduler state unavailable.");
        var restart = false;
        if (scheduler.Output.Trim() is { Length: > 0 } id)
        {
            if (id.Any(char.IsWhiteSpace)) throw new IOException("Ambiguous backup scheduler identity.");
            var inspected = await runner.RunAsync(["inspect", id], null, token);
            if (inspected.Code != 0) throw new IOException("Backup scheduler identity unavailable.");
            using var document = System.Text.Json.JsonDocument.Parse(inspected.Output);
            var container = document.RootElement[0];
            Preflight.VerifyRetainedResource(config, "container", container, root);
            if (container.GetProperty("Config").GetProperty("Image").GetString() != target.DatabaseImage ||
                container.GetProperty("Config").GetProperty("Labels").GetProperty("com.docker.compose.service").GetString() != "backup-scheduler")
                throw new IOException("Foreign backup scheduler refused.");
            restart = container.GetProperty("State").GetProperty("Running").GetBoolean();
        }
        if (restart && !policy.Enabled) throw new IOException("Disabled backup has a running scheduler.");
        await Required(BackupCompose.Command(root, config, "stop", "--timeout", "30", "backup-scheduler"), token);
        var next = config with { Backup = policy with { Source = corrected,
            Generation = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) } };
        next.Validate();
        BackupGeneration.Stage(root, next);
        await Required(BackupCompose.Command(root, next, "config", "--quiet"), token);
        BackupGeneration.Commit(root, next);
        BackupGeneration.Recover(root);
        if (restart) await Required(BackupCompose.Command(root, next, "up", "-d", "--no-deps", "--pull", "never", "backup-scheduler"), token);
        return true;
    }

    /// <summary>Version labels alone never grant this migration: only the two exact independently downloaded public bundles qualify.</summary>
    internal static void RequirePublicRelease(ReleaseBundle bundle)
    {
        ReleaseContract.RequireUse(bundle.Manifest, ReleaseCommands.OperatorVersion);
        if (bundle.Manifest.Status != "stable" || bundle.Manifest.Version != "1.9.21" || bundle.Manifest.Tag != "v1.9.21" ||
            bundle.Manifest.SourceRevision != "709a39ca7876fb4a08ce3090d79c4410efce09d8" || bundle.Manifest.LegacyCapture is not null ||
            bundle.Fingerprint != PublicFingerprint(bundle.Manifest.Platform))
            throw new IOException("Repair accepts only the immutable public v1.9.21 release.");
    }

    /// <summary>Unresolved, linked or mixed lifecycle/backup state must be recovered by its existing owner first.</summary>
    private static void RequireIdle(string root)
    {
        foreach (var path in new[] { RestoreReceipt.PathFor(root), UpdateReceipt.PathFor(root) })
            if (new FileInfo(path).LinkTarget is not null || Path.Exists(path) && !File.Exists(path))
                throw new IOException("Unsafe lifecycle receipt refused.");
        RestoreReceipt.RequireResolved(root);
        UpdateReceipt.RequireResolved(root);
        if (!InstallationCompletion.IsComplete(root)) throw new IOException("Repair requires a completed installation.");
        var completion = Path.Combine(root, File.Exists(Path.Combine(root, "setup-complete")) ? "setup-complete" : "restore-complete");
        ProtectedFiles.SafePath(completion);
        ProtectedFiles.Check(completion, 0);
        foreach (var name in new[] { "backup-transition.json", "installation.backup-next", "recovery-control/host-operation.json",
                     "recovery-control/restore-in-progress", "recovery-control/update-in-progress" })
        {
            var path = Path.Combine(root, name);
            if (Path.Exists(path) || new FileInfo(path).LinkTarget is not null) throw new IOException("Unresolved installation operation; use its retained owner.");
        }
        Deployment.CheckSecrets(root);
    }

    /// <summary>Read only the existing destination inode and installation marker; never prepare, probe or modify archive storage.</summary>
    private static void RequireDestination(BackupPolicy policy, Guid installation)
    {
        using var destination = new SafeDirectory(policy.Destination);
        var facts = destination.Identity;
        if (facts.DeviceMajor != policy.DeviceMajor || facts.DeviceMinor != policy.DeviceMinor || facts.Inode != policy.Inode)
            throw new IOException("Backup destination identity changed.");
        ProtectedFiles.Check(policy.Destination, 1654, directory: true);
        if (policy.Kind == "mounted")
        {
            using var parent = new SafeDirectory(Path.GetDirectoryName(policy.Destination)!);
            if (parent.Identity.Mount == facts.Mount) throw new IOException("Backup destination is unmounted.");
        }
        using var marker = destination.Read(".wayfarer-recovery");
        if (marker.Length > 128 || new StreamReader(marker).ReadToEnd() != $"wayfarer-recovery-v1\n{installation:D}\n")
            throw new IOException("Backup destination installation marker changed.");
    }

    /// <summary>A failed scheduler/config operation preserves protected metadata and any committed generation for explicit recovery.</summary>
    private async Task Required(string[] args, CancellationToken token)
    {
        if ((await runner.RunAsync(args, null, token)).Code != 0) throw new IOException("Backup source repair Compose operation failed.");
    }
}
