using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Bounded backup configuration transaction; the installation rename is the sole commit point.</summary>
public static class BackupGeneration
{
    private sealed record Transition(string Previous, string Next);

    /// <summary>Stage immutable derived inputs and preserve exact previous trusted bytes before committing.</summary>
    public static void Stage(string root, Deployment next)
    {
        var policy = next.Backup ?? throw new UsageException("Backup policy missing.");
        var directory = BackupCompose.DirectoryPath(root, policy);
        Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ProtectedFiles.Create(Path.Combine(directory, "compose.json"), BackupCompose.Render(root, next));
        // Docker mounts individual files; worker input needs only UID1654 readability, not host directory traversal.
        ProtectedFiles.Create(Path.Combine(directory, "worker.json"), JsonSerializer.Serialize(policy.Worker(next.Installation), WayfarerRecovery.ArchiveContract.Json), 1654);
        using var generation = new WayfarerRecovery.SafeDirectory(directory);
        generation.Flush();
        using var parent = new WayfarerRecovery.SafeDirectory(Path.GetDirectoryName(directory)!);
        parent.Flush();
    }

    /// <summary>Commit only after staged literal inputs and worker destination capability have passed preflight.</summary>
    public static void Commit(string root, Deployment next)
    {
        BackupCompose.Check(root, next);
        var previous = File.ReadAllText(Path.Combine(root, "installation.json"));
        var serialized = JsonSerializer.Serialize(next);
        ProtectedFiles.Create(Path.Combine(root, "backup-transition.json"), JsonSerializer.Serialize(new Transition(previous, serialized)));
        using var authority = new WayfarerRecovery.SafeDirectory(root);
        authority.Flush();
        ProtectedFiles.Create(Path.Combine(root, "installation.backup-next"), serialized);
        File.Move(Path.Combine(root, "installation.backup-next"), Path.Combine(root, "installation.json"), overwrite: true);
        authority.Flush();
        // Retain the previous generation for explicit interrupted-write recovery and diagnosis.
    }

    /// <summary>Recover only a receipted old/new installation, never adopt arbitrary mixed configuration.</summary>
    public static void Recover(string root)
    {
        var path = Path.Combine(root, "backup-transition.json");
        ProtectedFiles.Check(path, 0);
        if (new FileInfo(path).Length > 1048576) throw new UsageException("Backup transition exceeds its bound.");
        var transition = JsonSerializer.Deserialize<Transition>(File.ReadAllText(path)) ?? throw new UsageException("Invalid backup transition.");
        var current = File.ReadAllText(Path.Combine(root, "installation.json"));
        if (current != transition.Previous && current != transition.Next) throw new UsageException("Ambiguous interrupted backup configuration.");
        var retained = Path.Combine(root, "backup-previous.json");
        if (!File.Exists(retained)) ProtectedFiles.Create(retained, transition.Previous);
        using var authority = new WayfarerRecovery.SafeDirectory(root);
        authority.Flush();
        var pending = Path.Combine(root, "installation.backup-next");
        if (File.Exists(pending))
        {
            ProtectedFiles.Check(pending, 0);
            if (File.ReadAllText(pending) != transition.Next) throw new UsageException("Unknown staged configuration.");
            File.Delete(pending);
        }
        File.Delete(path);
        authority.Flush();
    }
}
