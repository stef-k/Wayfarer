using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Publish complete protected setup inputs before exposing any resumable installation files.</summary>
internal static class SetupProvisioning
{
    internal const string Name = "setup-provisioning";
    private static readonly (string Name, uint Owner)[] Files =
    [
        ("installation.json", 0), ("secrets/db-password", 0), ("secrets/db-app-password", 999),
        ("secrets/app-password", 1654), ("deployment.env", 0), ("setup-progress.json", 0)
    ];

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

    /// <summary>Accept only a complete initial receipt and byte-identical, safely owned files already published from it.</summary>
    internal static Deployment Load(string root)
    {
        var inputs = Path.Combine(root, Name);
        var config = Deployment.Load(root, inputs);
        var progress = SetupProgress.Load(inputs, config);
        if (config.Backup is not null || config.Installation != Guid.Empty || config.StorageGeneration is not null ||
            progress.Completed != 0 || progress.AdminStarted)
            throw new UsageException("Protected provisioning is not an initial setup snapshot.");
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

    /// <summary>Finish an interrupted publication without regenerating credentials or replacing any existing file.</summary>
    internal static SetupProgress Resume(string root, Action<string>? checkpoint = null)
    {
        var config = Load(root);
        var inputs = Path.Combine(root, Name);
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
        // Only a fully verified canonical receipt permits reclaiming this invocation's redundant snapshot.
        Deployment.Load(root);
        var progress = SetupProgress.Load(root, config);
        using (var directory = new SafeDirectory(inputs)) directory.Clear();
        Directory.Delete(inputs);
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
