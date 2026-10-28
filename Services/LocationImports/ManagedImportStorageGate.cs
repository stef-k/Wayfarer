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
        var handle = OpenControl(control);
        try
        {
            if (Flock(handle, 2 | 4) != 0)
            {
                var error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                if (error == 11) return null;
                throw new IOException("Managed import exclusion unavailable.");
            }
            var identity = Identity(handle, initialize: true);
            // Independent libc opens avoid .NET's own flock sharing probe conflicting with our exclusive flock.
            using var current = OpenControl(control);
            if (Identity(current) != identity) throw new IOException("Managed import control changed.");
            return new GateLease(handle, identity);
        }
        catch { handle.Dispose(); throw; }
    }

    /// <summary>O_CLOEXEC/O_NOFOLLOW ensure the gate cannot escape into children or follow a replaced symlink.</summary>
    private static SafeFileHandle OpenControl(string path)
    {
        var descriptor = Open(path, 2 | 0x40 | 0x80000 | 0x20000, 0x180);
        if (descriptor < 0) throw new IOException("Managed import control unavailable.");
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    /// <summary>Checks Linux filesystem type, device/inode and boot identity; configured attestation covers topology.</summary>
    private static string Identity(SafeFileHandle handle, bool initialize = false)
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
            if ((Marshal.ReadInt16(buffer, 28) & 0x3f) != 0) throw new IOException("Managed import control must be private.");
            var bytes = new byte[32];
            var read = RandomAccess.Read(handle, bytes, 0);
            if (read == 0 && initialize)
            {
                bytes = System.Text.Encoding.ASCII.GetBytes(Guid.NewGuid().ToString("N"));
                RandomAccess.Write(handle, bytes, 0);
                RandomAccess.FlushToDisk(handle);
                read = bytes.Length;
            }
            var store = System.Text.Encoding.ASCII.GetString(bytes);
            if (read != 32 || !Guid.TryParseExact(store, "N", out _)) throw new IOException("Managed import identity unavailable.");
            // Persistent random identity distinguishes separate local roots; normal host/process restarts retain it.
            return $"linux-local:{store}:{major}:{minor}:{inode}";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    /// <summary>Owns the non-inherited descriptor until the synchronous first-open/removal block is irrevocably over.</summary>
    internal sealed class GateLease(SafeFileHandle handle, string identity) : IDisposable
    {
        internal string StoreIdentity { get; } = identity;
        public void Dispose() => handle.Dispose();
    }

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags, uint mode);

    [DllImport("libc", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle fd, int operation);
    [DllImport("libc", EntryPoint = "fstatfs", SetLastError = true)]
    private static extern int Fstatfs(SafeFileHandle fd, IntPtr buffer);
    [DllImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static extern int Statx(SafeFileHandle fd, string path, int flags, uint mask, IntPtr buffer);
}
