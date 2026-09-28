using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Explicit offline import, validation, source export and metadata-only adoption.</summary>
public sealed class ReleaseCommands(IProcessRunner runner, ITerminal terminal)
{
    public static string OperatorVersion => typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    /// <summary>All local-artifact grammar is checked before filesystem or Docker work.</summary>
    public static void Validate(string[] args)
    {
        if (args is ["inspect" or "verify-images" or "import" or "adopt", var path]) { BackupPolicy.LiteralPath(path); return; }
        if (args is ["target", var bundle, var project]) { BackupPolicy.LiteralPath(bundle);
            if (System.Text.RegularExpressions.Regex.IsMatch(project, "\\A[a-z0-9][a-z0-9_-]{0,62}\\z")) return; }
        if (args is ["reconcile", var stage] && System.Text.RegularExpressions.Regex.IsMatch(stage, "\\A\\.stage-[a-f0-9]{32}\\z")) return;
        throw new UsageException("Use release inspect|verify-images|import|adopt /absolute/bundle, target /absolute/bundle project, or reconcile .stage-ID.");
    }

    /// <summary>Inspection and target export never claim images are execution-ready.</summary>
    public async Task<int> RunAsync(string root, string[] args, CancellationToken token)
    {
        Validate(args);
        if (args[0] == "reconcile")
        {
            using var exclusion = Setup.Lock(root);
            Describe(ReleaseStore.Reconcile(root, args[1]), false);
            return 0;
        }
        var bundle = ReleaseBundle.Validate(args[1]);
        if (args[0] == "target")
        {
            terminal.Write(JsonSerializer.Serialize(bundle.Target(args[2]), ArchiveContract.Json));
            return 0;
        }
        if (args[0] == "inspect") { Describe(bundle, false); return 0; }
        ReleaseContract.RequireUse(bundle.Manifest, OperatorVersion);
        var available = await new ReleaseImagesVerifier(runner).VerifyAsync(bundle, token);
        if (args[0] == "verify-images") { Describe(bundle, available); return available ? 0 : 1; }
        if (args[0] == "import")
        {
            using var exclusion = Setup.Lock(root);
            Describe(ReleaseStore.Import(root, args[1]), available);
            return 0;
        }
        if (!available) throw new UsageException("Adoption requires all local images to be verified.");
        using (var exclusion = Setup.Lock(root))
        {
            // Pin protected installed bytes before metadata adoption; mutable input cannot race the image probes.
            var installed = ReleaseStore.Import(root, bundle.Directory);
            if (installed.Fingerprint != bundle.Fingerprint) throw new IOException("Release changed before adoption.");
            Adopt(root, installed);
        }
        Describe(bundle, true);
        return 0;
    }

    /// <summary>Current bytes must agree; only the installation's new release reference is atomically added.</summary>
    private static void Adopt(string root, ReleaseBundle bundle)
    {
        RestoreReceipt.RequireResolved(root);
        if (!InstallationCompletion.IsComplete(root) || File.Exists(Path.Combine(root, "backup-transition.json")) ||
            File.Exists(Path.Combine(root, "recovery-control/host-operation.json"))) throw new IOException("Unresolved installation operation.");
        var config = Deployment.Load(root);
        using var recovery = config.Backup is null ? null : new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
        ReleaseDispatch.RequireExecutable(bundle);
        bundle.Corroborate(config);
        if (config.Backup is { } backup)
        {
            backup.CheckPayload();
            if (backup.PayloadSha256 != bundle.Target(config.Project).PayloadFingerprint) throw new IOException("Current recovery payload differs.");
            bundle.Corroborate(backup.Source);
        }
        var installed = ReleaseStore.Import(root, bundle.Directory);
        var authority = ReleaseAuthority.From(installed);
        if (config.Release is not null && config.Release != authority) throw new IOException("Adoption cannot switch release authority.");
        var next = config with { Schema = 4, Release = authority };
        next.Validate();
        var temporary = Path.Combine(root, "installation-release-" + Guid.NewGuid().ToString("N") + ".json");
        ProtectedFiles.Create(temporary, JsonSerializer.Serialize(next));
        File.Move(temporary, Path.Combine(root, "installation.json"), true);
        using var directory = new SafeDirectory(root);
        directory.Flush();
    }

    private void Describe(ReleaseBundle bundle, bool ready) => terminal.Write(JsonSerializer.Serialize(new
    {
        Release = bundle.Manifest.Name, bundle.Fingerprint, bundle.Manifest.Status,
        Integrity = "validated", Images = ready ? "execution-ready" : "not-qualified", Publisher = "administrator-trusted-offline"
    }));
}
