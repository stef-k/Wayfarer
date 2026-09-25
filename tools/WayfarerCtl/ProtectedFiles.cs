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
            if ((stat.Mode & 0xF000) == 0xA000 || stat.User != 0 || (stat.Mode & 0x12) != 0)
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

    /// <summary>Create without replacement; set mode on open before writing any secret bytes.</summary>
    public static void Create(string path, string content, uint owner = 0)
    {
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = PrivateFile
        });
        using var writer = new StreamWriter(stream, leaveOpen: true);
        writer.Write(content);
        writer.Flush();
        stream.Flush(flushToDisk: true);
        if (chown(path, owner, owner) != 0) throw new IOException("Cannot assign protected file ownership.");
    }

    /// <summary>Separate consumer files carry the same role password; bootstrap uses independent entropy.</summary>
    public static void CreateSecrets(string root)
    {
        var directory = Path.Combine(root, "secrets");
        if (Path.Exists(directory)) throw new UsageException("Existing secrets require explicit recovery; refusing overwrite.");
        Directory.CreateDirectory(directory, PrivateDirectory);
        var app = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        Create(Path.Combine(directory, "db-password"), Convert.ToHexString(RandomNumberGenerator.GetBytes(32)));
        Create(Path.Combine(directory, "db-app-password"), app, 999);
        Create(Path.Combine(directory, "app-password"), app, 1654);
    }

    private static Stat Inspect(string path)
    {
        if (statx(-100, path, 0x100, 0x7ff, out var stat) != 0) throw new UsageException("Required protected path is unavailable.");
        return stat;
    }
}
