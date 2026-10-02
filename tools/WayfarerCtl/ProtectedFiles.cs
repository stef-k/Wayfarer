using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace WayfarerCtl;

/// <summary>Linux ownership/link checks and exclusive protected creation for installation state.</summary>
public static class ProtectedFiles
{
    public const UnixFileMode PrivateFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    public const UnixFileMode PrivateDirectory = PrivateFile | UnixFileMode.UserExecute;

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Stat
    {
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint User;
        [FieldOffset(24)] public uint Group;
        [FieldOffset(28)] public ushort Mode;
    }
    [DllImport("libc", SetLastError = true)]
    private static extern int statx(int directory, string path, int flags, uint mask, out Stat result);
    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string path, uint user, uint group);
    [DllImport("libc")]
    private static extern uint geteuid();

    /// <summary>Fresh host installation needs root for consumer-specific UID ownership.</summary>
    public static void RequireRoot()
    {
        if (geteuid() != 0) throw new UsageException("Run as root to verify/provision protected installation files.");
    }

    /// <summary>Reject links and writable ancestors before trusting any root-owned bundle/config path.</summary>
    public static void SafePath(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if (!Path.Exists(current) && !File.Exists(current) && !Directory.Exists(current))
            {
                if (new FileInfo(current).LinkTarget is not null) throw new UsageException("Symbolic links are not allowed.");
                continue;
            }
            var stat = Inspect(current);
            if ((stat.Mode & 0xF000) == 0xA000 || stat.User != 0 || ((stat.Mode & 0x12) != 0 && (stat.Mode & 0xF200) != 0x4200))
                throw new UsageException("Installation paths must be root-owned, link-free and not group/world-writable.");
        }
    }

    /// <summary>Check real regular files, single links, exact mode and selected container identity.</summary>
    public static void Check(string path, uint owner, bool directory = false)
    {
        var stat = Inspect(path);
        var expected = directory ? 0x4000 | 0x1C0 : 0x8000 | 0x180;
        if (stat.Mode != expected || stat.User != owner || stat.Group != owner || (!directory && stat.Links != 1))
            throw new UsageException("Protected file ownership, type or mode is unsafe.");
    }

    /// <summary>Only the generated root:1654/0640 delegation in a root:root/0755 control parent is worker-readable authority.</summary>
    public static void CheckRecoveryReservation(string path)
    {
        SafePath(path);
        var parent = Inspect(Path.GetDirectoryName(path)!);
        var file = Inspect(path);
        if (parent.Mode != (0x4000 | 0x1ED) || parent.User != 0 || parent.Group != 0 ||
            file.Mode != (0x8000 | 0x1A0) || file.User != 0 || file.Group != 1654 || file.Links != 1)
            throw new UsageException("Recovery reservation ownership, type or mode is unsafe.");
    }

    /// <summary>Create without replacement; start private, optionally granting a separate read-only consumer group.</summary>
    public static void Create(string path, string content, uint owner = 0, uint? readerGroup = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateFile
        });
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
        if (chown(path, owner, readerGroup ?? owner) != 0) throw new IOException("Cannot assign protected file ownership.");
        if (readerGroup is not null) File.SetUnixFileMode(path, PrivateFile | UnixFileMode.GroupRead);
    }

    /// <summary>Separate consumer files carry the same role password; an optional observer exercises interrupted provisioning.</summary>
    public static void CreateSecrets(string root, Action<string>? created = null)
    {
        if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
        var directory = Path.Combine(root, "secrets");
        if (Path.Exists(directory)) throw new UsageException("Existing secrets require explicit recovery; refusing overwrite.");
        Directory.CreateDirectory(directory, PrivateDirectory);
        var app = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Create(Path.Combine(directory, "db-password"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        created?.Invoke(Path.Combine(directory, "db-password"));
        Create(Path.Combine(directory, "db-app-password"), app, 999);
        created?.Invoke(Path.Combine(directory, "db-app-password"));
        Create(Path.Combine(directory, "app-password"), app, 1654);
        created?.Invoke(Path.Combine(directory, "app-password"));
    }

    /// <summary>Fingerprint distinct local credential files without placing secret bytes in restore plans or receipts.</summary>
    public static string SecretsFingerprint(string root)
    {
        Deployment.CheckSecrets(root);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var name in new[] { "db-password", "db-app-password", "app-password" })
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(Path.Combine(root, "secrets", name))));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Finish secret provisioning only for an explicitly receipted clean-root restore, preserving every existing byte.</summary>
    public static void ResumeRestoreSecrets(string root)
    {
        var receipt = RestoreReceipt.Load(root);
        if (receipt is not { Plan.NewInstall: true, Phase: RestorePhase.Authorized })
            throw new UsageException("New restore intent required for secret provisioning.");
        var directory = Path.Combine(root, "secrets");
        Directory.CreateDirectory(directory, PrivateDirectory);
        Check(directory, 0, directory: true);
        string? app = null;
        foreach (var (name, owner) in new[] { ("app-password", 1654u), ("db-app-password", 999u) })
        {
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) continue;
            Check(path, owner);
            var value = File.ReadAllText(path);
            if (!System.Text.RegularExpressions.Regex.IsMatch(value, "^[A-F0-9]{64}$") || app is not null && value != app)
                throw new UsageException("Existing restore secrets disagree.");
            app = value;
        }
        app ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        foreach (var (name, owner) in new[] { ("app-password", 1654u), ("db-app-password", 999u) })
            if (!File.Exists(Path.Combine(directory, name))) Create(Path.Combine(directory, name), app, owner);
        if (!File.Exists(Path.Combine(directory, "db-password")))
            Create(Path.Combine(directory, "db-password"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Deployment.CheckSecrets(root);
        using var parent = new WayfarerRecovery.SafeDirectory(directory);
        parent.Flush();
    }

    private static Stat Inspect(string path)
    {
        if (statx(-100, path, 0x100, 0x7ff, out var stat) != 0) throw new UsageException("Required protected path is unavailable.");
        return stat;
    }
}
