using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Publish complete protected setup inputs before exposing any resumable installation files.</summary>
internal static class SetupProvisioning
{
    internal const string Name = "setup-provisioning";
    /// <summary>Only this post-handoff private name may contain partially reclaimed duplicates.</summary>
    internal const string ReclamationName = "setup-provisioning-reclaim";
    private static readonly (string Name, uint Owner)[] Files =
    [
        ("installation.json", 0), ("secrets/db-password", 0), ("secrets/db-app-password", 999),
        ("secrets/app-password", 1654), ("deployment.env", 0), ("setup-progress.json", 0)
    ];

    /// <summary>Either snapshot phase blocks fresh setup, including unsafe or dangling linked names.</summary>
    internal static bool IsPending(string root) => new[] { Name, ReclamationName }.Any(name => Present(Path.Combine(root, name)));

    /// <summary>Include dangling links in protected-state detection so they cannot be treated as absent.</summary>
    private static bool Present(string path) => Path.Exists(path) || new FileInfo(path).LinkTarget is not null;

    /// <summary>Incomplete private preparation stays under releases and cannot strand the plain setup entry path.</summary>
    internal static SetupProgress Create(string root, Deployment config, Action<string>? checkpoint = null)
    {
        Setup.RequireFreshState(root);
        var releases = Path.Combine(root, "releases");
        Directory.CreateDirectory(releases, ProtectedFiles.PrivateDirectory);
        ProtectedFiles.Check(releases, 0, directory: true);
        var stage = Path.Combine(releases, ".setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage, ProtectedFiles.PrivateDirectory);
        var installation = Path.Combine(stage, "installation.json");
        ProtectedFiles.Create(installation, JsonSerializer.Serialize(config));
        checkpoint?.Invoke(installation);
        ProtectedFiles.CreateSecrets(stage, checkpoint);
        var environment = Path.Combine(stage, "deployment.env");
        ProtectedFiles.Create(environment, config.EnvironmentFile(root));
        checkpoint?.Invoke(environment);
        checkpoint?.Invoke(Path.Combine(stage, "setup-progress.json"));
        SetupProgress.Create(stage, config);
        // Validate the complete snapshot before its atomic publication becomes recovery authority.
        Deployment.Load(root, stage);
        SetupProgress.Load(stage, config);
        Flush(Path.Combine(stage, "secrets"));
        Flush(stage);
        Directory.Move(stage, Path.Combine(root, Name));
        Flush(root);
        Flush(releases);
        return Resume(root, checkpoint);
    }

    /// <summary>Publication requires a complete snapshot; private reclamation requires independently verified canonical inputs.</summary>
    internal static Deployment Load(string root)
    {
        var reclaim = Path.Combine(root, ReclamationName);
        if (Present(Path.Combine(root, Name)) && Present(reclaim))
            throw new UsageException("Ambiguous protected setup snapshots; preserve them for reconciliation.");
        var reclaiming = Present(reclaim);
        var inputs = reclaiming ? root : Path.Combine(root, Name);
        var config = Deployment.Load(root, inputs);
        var progress = SetupProgress.Load(inputs, config);
        if (config.Backup is not null || config.Installation != Guid.Empty || config.StorageGeneration is not null ||
            progress.Completed != 0 || progress.AdminStarted)
            throw new UsageException("Protected provisioning is not an initial setup snapshot.");
        if (reclaiming)
        {
            CheckReclamation(root, reclaim);
            return config;
        }
        if (!Directory.EnumerateFileSystemEntries(inputs).Select(Path.GetFileName).Order().SequenceEqual(
                new[] { "deployment.env", "installation.json", "secrets", "setup-progress.json" }) ||
            !Directory.EnumerateFileSystemEntries(Path.Combine(inputs, "secrets")).Select(Path.GetFileName).Order().SequenceEqual(
                new[] { "app-password", "db-app-password", "db-password" }))
            throw new UsageException("Unrecognized protected provisioning files; refusing continuation.");
        var secrets = Path.Combine(root, "secrets");
        if (Path.Exists(secrets) || new DirectoryInfo(secrets).LinkTarget is not null)
            ProtectedFiles.Check(secrets, 0, directory: true);
        foreach (var (name, owner) in Files)
        {
            var target = Path.Combine(root, name);
            if (!Path.Exists(target) && new FileInfo(target).LinkTarget is null) continue;
            ProtectedFiles.Check(target, owner);
            if (!File.ReadAllBytes(target).SequenceEqual(File.ReadAllBytes(Path.Combine(inputs, name))))
                throw new UsageException("Existing protected setup files differ; refusing overwrite.");
        }
        return config;
    }

    /// <summary>Verify every remaining duplicate before cleanup; foreign names, ownership, links or changed bytes are preserved.</summary>
    private static void CheckReclamation(string root, string path)
    {
        ProtectedFiles.SafePath(path);
        ProtectedFiles.Check(path, 0, directory: true);
        using var directory = new SafeDirectory(path);
        if (directory.Names().Except(new[] { "installation.json", "deployment.env", "setup-progress.json", "secrets" }).Any())
            throw new UsageException("Unrecognized setup reclamation files; preserve them for reconciliation.");
        if (Present(Path.Combine(path, "secrets")))
        {
            ProtectedFiles.Check(Path.Combine(path, "secrets"), 0, directory: true);
            using var secrets = directory.Child("secrets");
            if (secrets.Names().Except(new[] { "db-password", "db-app-password", "app-password" }).Any())
                throw new UsageException("Unrecognized setup reclamation credentials; preserve them for reconciliation.");
        }
        foreach (var (name, owner) in Files)
        {
            if (!Present(Path.Combine(path, name))) continue;
            ProtectedFiles.Check(Path.Combine(path, name), owner);
            using var file = directory.Read(name);
            using var content = new MemoryStream();
            file.CopyTo(content);
            if (!content.ToArray().SequenceEqual(File.ReadAllBytes(Path.Combine(root, name))))
                throw new UsageException("Setup reclamation inputs differ; preserve them for reconciliation.");
        }
    }

    /// <summary>Finish publication, durably transfer its authority to private reclamation, then remove only verified duplicates.</summary>
    internal static SetupProgress Resume(string root, Action<string>? checkpoint = null)
    {
        var config = Load(root);
        var inputs = Path.Combine(root, Name);
        if (Present(inputs))
        {
            var secrets = Path.Combine(root, "secrets");
            Directory.CreateDirectory(secrets, ProtectedFiles.PrivateDirectory);
            ProtectedFiles.Check(secrets, 0, directory: true);
            Flush(root);
            foreach (var (name, owner) in Files)
            {
                var target = Path.Combine(root, name);
                if (!Path.Exists(target)) Publish(target, File.ReadAllText(Path.Combine(inputs, name)), owner);
                checkpoint?.Invoke(target);
            }
            // Recheck all published bytes and the canonical receipt before the create-only atomic handoff.
            Deployment.Load(root);
            SetupProgress.Load(root, config);
            Load(root);
            using var parent = new SafeDirectory(root);
            parent.Publish(Name, ReclamationName);
        }
        var progress = SetupProgress.Load(root, config);
        // A previous rename's flush may have failed; make the observed handoff durable before any deletion.
        Flush(root);
        var reclaim = Path.Combine(root, ReclamationName);
        checkpoint?.Invoke(reclaim);
        using (var directory = new SafeDirectory(reclaim)) directory.Clear();
        Directory.Delete(reclaim);
        Flush(root);
        return progress;
    }

    /// <summary>Flush one private temporary file, then publish its final name atomically without replacement.</summary>
    private static void Publish(string path, string content, uint owner)
    {
        var temporary = "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N");
        var parent = Path.GetDirectoryName(path)!;
        using var directory = new SafeDirectory(parent);
        try
        {
            ProtectedFiles.Create(Path.Combine(parent, temporary), content, owner);
            directory.Publish(temporary, Path.GetFileName(path));
        }
        catch
        {
            // Cleanup owns only this temporary name and must preserve the primary publication failure.
            try { if (Path.Exists(Path.Combine(parent, temporary))) directory.Delete(temporary); }
            catch { }
            throw;
        }
    }

    /// <summary>Directory entry durability keeps a complete recovery snapshot ahead of canonical file publication.</summary>
    private static void Flush(string path)
    {
        using var directory = new SafeDirectory(path);
        directory.Flush();
    }
}
