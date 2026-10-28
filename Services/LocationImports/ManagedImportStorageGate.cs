using System.Runtime.InteropServices;
using Microsoft.Extensions.Options;
using Microsoft.Win32.SafeHandles;

namespace Wayfarer.Services.LocationImports;

/// <summary>Explicit qualification of this installation's cooperating single-kernel private local store.</summary>
public sealed class ManagedImportOptions
{
    /// <summary>Operator attestation; false until store sharing, permissions and flock exclusion are qualified.</summary>
    public bool LocalStoreQualified { get; set; }
}

/// <summary>Independent Linux flock handles exclude first creation and finalized absence even after DB-session loss.</summary>
public sealed class ManagedImportStorageGate(StoragePaths paths, IOptions<ManagedImportOptions> options)
{
    /// <summary>Opens a stable control inode outside imports; never rotates/unlinks it. No DB locks may be held here.</summary>
    internal GateLease? TryAcquire()
    {
        if (!OperatingSystem.IsLinux() || !options.Value.LocalStoreQualified) return null;
        Directory.CreateDirectory(paths.Uploads);
        var control = Path.Combine(paths.Uploads, ".location-import.lock");
        var stream = new FileStream(control, new FileStreamOptions
        {
            Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.ReadWrite,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite
        });
        try
        {
            if (Flock(stream.SafeFileHandle, 2 | 4) != 0) { stream.Dispose(); return null; }
            var identity = Identity(stream.SafeFileHandle);
            // An administrator replacement is unavailable, never authority to finalize an old path's absence.
            using var current = File.OpenHandle(control, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (Identity(current) != identity) throw new IOException("Managed import control changed.");
            return new GateLease(stream, identity);
        }
        catch { stream.Dispose(); throw; }
    }

    /// <summary>Checks Linux filesystem type, device/inode and boot identity; configured attestation covers topology.</summary>
    private static string Identity(SafeFileHandle handle)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (Fstatfs(handle, buffer) != 0) throw new IOException("Managed import storage unavailable.");
            var type = Marshal.ReadInt64(buffer);
            // ext, XFS, Btrfs, tmpfs, overlayfs and ZFS; network/unknown adapters fail closed.
            if (type is not 0xEF53 and not 0x58465342 and not 0x9123683E and not 0x01021994
                and not 0x794c7630 and not 0x2FC12FC1) throw new IOException("Managed import storage unsupported.");
            if (Statx(handle, "", 0x1000, 0x7ff, buffer) != 0) throw new IOException("Managed import identity unavailable.");
            var inode = Marshal.ReadInt64(buffer, 32);
            var major = Marshal.ReadInt32(buffer, 136);
            var minor = Marshal.ReadInt32(buffer, 140);
            var boot = File.ReadAllText("/proc/sys/kernel/random/boot_id").Trim();
            // A kernel/store change requires quiescent requalification; process restarts retain this identity.
            return $"linux-local:{boot}:{major}:{minor}:{inode}";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Owns the non-inherited descriptor until the synchronous first-open/removal block is irrevocably over.</summary>
    internal sealed class GateLease(FileStream stream, string identity) : IDisposable
    {
        internal string StoreIdentity { get; } = identity;
        public void Dispose() => stream.Dispose();
    }

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle fd, int operation);
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int Fstatfs(SafeFileHandle fd, IntPtr buffer);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(SafeFileHandle fd, string path, int flags, uint mask, IntPtr buffer);
}
