using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Public prefetch and explicit offline import, validation, source export and metadata-only adoption.</summary>
public sealed class ReleaseCommands(IProcessRunner runner, ITerminal terminal)
{
    public static string OperatorVersion => typeof(Cli).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

    /// <summary>All local-artifact grammar is checked before filesystem or Docker work.</summary>
    public static void Validate(string[] args)
    {
        if (args is ["unpack", var archive, var stagePath])
        {
            if (Path.IsPathFullyQualified(archive)) BackupPolicy.LiteralPath(archive);
            else PublicRelease.Selector(archive);
            BackupPolicy.LiteralPath(stagePath);
            return;
        }
        if (args is ["acquire", var selector]) { PublicRelease.Selector(selector); return; }
        if (args is ["inspect" or "verify-images" or "import" or "adopt", var path]) { BackupPolicy.LiteralPath(path); return; }
        if (args is ["target", _, _, "current" or "legacy"]) { Validate(args[..3]); return; }
        if (args is ["corroborate", var input, var evidence]) { BackupPolicy.LiteralPath(input); BackupPolicy.LiteralPath(evidence); return; }
        if (args is ["target", var bundle, var project]) { BackupPolicy.LiteralPath(bundle);
            if (System.Text.RegularExpressions.Regex.IsMatch(project, "\\A[a-z0-9][a-z0-9_-]{0,62}\\z")) return; }
        if (args is ["reconcile", var stage] && System.Text.RegularExpressions.Regex.IsMatch(stage, "\\A\\.stage-[a-f0-9]{32}\\z")) return;
        throw new UsageException("Use release acquire X.Y.Z|latest, inspect|verify-images|import|adopt /absolute/bundle, unpack ARCHIVE STAGE, target BUNDLE PROJECT, or reconcile .stage-ID.");
    }

    /// <summary>Inspection and target export never claim images are execution-ready.</summary>
    public async Task<int> RunAsync(string root, string[] args, CancellationToken token)
    {
        Validate(args);
        if (args[0] == "unpack")
        {
            // Offline authoring/bootstrap helper: the caller owns private empty staging, never installed placement.
            if (!Directory.Exists(args[2]) || Directory.EnumerateFileSystemEntries(args[2]).Any())
                throw new UsageException("Unpack requires an existing empty private staging directory.");
            using var stage = new SafeDirectory(args[2]);
            stage.RequireLocalControl();
            var staged = Path.IsPathFullyQualified(args[1])
                ? await ReleaseArchive.ExtractAsync(args[1], args[2], token)
                : await PublicReleaseAcquisition.StageAsync(args[2], args[1], token);
            Describe(staged, false);
            return 0;
        }
        if (args[0] == "reconcile")
        {
            using var exclusion = Setup.Lock(root);
            Describe(ReleaseStore.Reconcile(root, args[1]), false);
            return 0;
        }
        if (args[0] == "acquire")
        {
            ProtectedFiles.SafePath(root);
            Directory.CreateDirectory(root, ProtectedFiles.PrivateDirectory);
            ProtectedFiles.Check(root, 0, directory: true);
            using var exclusion = Setup.Lock(root);
            await new Preflight(runner).DockerAsync(token);
            var acquired = await new PublicReleaseAcquisition(runner).AcquireAsync(root, args[1], token);
            terminal.Write(JsonSerializer.Serialize(new { Path = acquired.Directory, acquired.Fingerprint,
                acquired.Manifest.Version, Images = "execution-ready", Publisher = "public-stable-GitHub", Integrity = "GitHub-asset-SHA256" }));
            return 0;
        }
        var bundle = ReleaseBundle.Validate(args[1]);
        if (args[0] == "corroborate")
        {
            using var parent = new SafeDirectory(Path.GetDirectoryName(args[2])!);
            using var input = parent.Read(Path.GetFileName(args[2]));
            if (input.Length > ArchiveContract.ManifestLimit) throw new IOException("Source evidence exceeds bound.");
            bundle.Corroborate(JsonSerializer.Deserialize<SourceIdentity>(input, ArchiveContract.Json)
                ?? throw new IOException("Missing source evidence."));
            Describe(bundle, false);
            return 0;
        }
        if (args[0] == "target")
        {
            terminal.Write(JsonSerializer.Serialize(bundle.Target(args[2], args is [_, _, _, "current"]), ArchiveContract.Json));
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
        UpdateReceipt.RequireResolved(root);
        if (!InstallationCompletion.IsComplete(root) || File.Exists(Path.Combine(root, "backup-transition.json")) ||
            File.Exists(Path.Combine(root, "recovery-control/host-operation.json"))) throw new IOException("Unresolved installation operation.");
        var config = Deployment.Load(root);
        using var recovery = config.Backup is null ? null : new RecoveryLock(Path.Combine(root, "recovery-control/recovery.lock"));
        ReleaseDispatch.RequireExecutable(bundle);
        bundle.Corroborate(config);
        if (config.Backup is { } backup)
        {
            backup.CheckPayload();
            if (backup.PayloadSha256 != backup.Source.PayloadFingerprint) throw new IOException("Current recovery payload differs.");
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
