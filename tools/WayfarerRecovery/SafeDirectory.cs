using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WayfarerRecovery;

/// <summary>Linux handle-relative no-follow access; names never authorize traversal across links or mounts.</summary>
public sealed class SafeDirectory : IDisposable
{
    private readonly SafeFileHandle handle;
    private const int ReadOnly = 0, ReadWrite = 2, Create = 0x40, Exclusive = 0x80;
    private static readonly (int Directory, int NoFollow) Flags = NativePlatform.OpenFlags(RuntimeInformation.ProcessArchitecture);
    private static int DirectoryFlag => Flags.Directory;
    private static int NoFollow => Flags.NoFollow;
    private const int CloseOnExec = 0x80000;
    private const ulong Beneath = 8, NoLinks = 4, NoMounts = 1;

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow { public ulong Flags; public ulong Mode; public ulong Resolve; }

    /// <summary>Stable statx facts used to reject unsupported files and detect observed source changes.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    public struct Facts : IEquatable<Facts>
    {
        [FieldOffset(16)] public uint Links;
        [FieldOffset(20)] public uint User;
        [FieldOffset(24)] public uint Group;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(96)] public long ChangedSeconds;
        [FieldOffset(104)] public uint ChangedNanos;
        [FieldOffset(112)] public long ModifiedSeconds;
        [FieldOffset(120)] public uint ModifiedNanos;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
        [FieldOffset(144)] public ulong Mount;
        public readonly bool IsDirectory => (Mode & 0xf000) == 0x4000;
        public readonly bool IsFile => (Mode & 0xf000) == 0x8000 && Links == 1;
        public readonly bool Equals(Facts other) => Inode == other.Inode && Size == other.Size && Mode == other.Mode &&
            Links == other.Links && DeviceMajor == other.DeviceMajor && DeviceMinor == other.DeviceMinor &&
            ChangedSeconds == other.ChangedSeconds && ChangedNanos == other.ChangedNanos &&
            ModifiedSeconds == other.ModifiedSeconds && ModifiedNanos == other.ModifiedNanos;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, int directory, string path, ref OpenHow how, nuint size);
    [DllImport("libc", SetLastError = true)]
    private static extern int statx(SafeFileHandle descriptor, string path, int flags, uint mask, out Facts facts);
    [DllImport("libc", SetLastError = true)]
    private static extern int renameat2(SafeFileHandle source, string name, SafeFileHandle target, string destination, uint flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int unlinkat(SafeFileHandle descriptor, string name, int flags);
    [StructLayout(LayoutKind.Explicit, Size = 120)]
    private struct FileSystemFacts
    {
        [FieldOffset(0)] public long Type;
        [FieldOffset(8)] public long BlockSize;
        [FieldOffset(32)] public ulong AvailableBlocks;
    }
    [DllImport("libc", SetLastError = true)]
    private static extern int fstatfs(SafeFileHandle descriptor, out FileSystemFacts facts);

    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(SafeFileHandle descriptor);

    /// <summary>Resolve an absolute root without following any symlink ancestor.</summary>
    public SafeDirectory(string path)
    {
        _ = NativePlatform.Current;
        if (!Path.IsPathFullyQualified(path)) throw new IOException("Absolute directory required.");
        handle = Open(-100, path, ReadOnly | DirectoryFlag, NoLinks);
    }

    private SafeDirectory(SafeFileHandle descriptor) => handle = descriptor;

    /// <summary>Reject ambiguous archive and directory-relative names before kernel resolution.</summary>
    public static void ValidateName(string name)
    {
        if (name.Length is 0 or > 2048 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') ||
            name.Any(char.IsControl) || name.Split('/').Any(part => part is "" or "." or "..") || name.Count(c => c == '/') > 32)
            throw new IOException("Unsafe relative name.");
    }

    private static SafeFileHandle Open(int parent, string name, int flags, ulong resolve, ulong mode = 0)
    {
        var how = new OpenHow { Flags = (ulong)(flags | NoFollow | CloseOnExec), Resolve = resolve, Mode = mode };
        var descriptor = syscall(437, parent, name, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (descriptor < 0) throw new IOException("Safe filesystem access failed.");
        return new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
    }

    /// <summary>Inspect an open inode, never a path that may have been replaced since validation.</summary>
    public static Facts Inspect(SafeFileHandle descriptor)
    {
        if (statx(descriptor, "", 0x1000, 0x17ff, out var facts) != 0) throw new IOException("Cannot inspect open inode.");
        return facts;
    }

    public Facts Identity => Inspect(handle);

    /// <summary>Recovery control must reside on a supported persistent local Linux filesystem, never NAS/FUSE.</summary>
    public void RequireLocalControl()
    {
        if (fstatfs(handle, out var facts) != 0 || facts.Type is not (0xef53 or 0x58465342 or 0x9123683e or 0x794c7630 or 0x2fc12fc1))
            throw new IOException("Recovery control requires a persistent local Linux filesystem.");
    }

    /// <summary>Enumerate names only; each subsequent open independently enforces links/mount/type rules.</summary>
    public string[] Names(int limit = ArchiveContract.EntryLimit)
    {
        var names = Directory.EnumerateFileSystemEntries($"/proc/self/fd/{handle.DangerousGetHandle()}")
            .Take(limit + 1).Select(Path.GetFileName).Select(name => name!).ToArray();
        if (names.Length > limit) throw new IOException("Directory scan limit exceeded.");
        return names.Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>Open a child directory on this same mount, retaining its handle throughout traversal.</summary>
    public SafeDirectory Child(string name)
    {
        ValidateName(name);
        return new SafeDirectory(Open(handle.DangerousGetHandle().ToInt32(), name, DirectoryFlag, Beneath | NoLinks | NoMounts));
    }

    /// <summary>Read a single-link regular file without crossing another mount.</summary>
    public FileStream Read(string name)
    {
        ValidateName(name);
        var file = Open(handle.DangerousGetHandle().ToInt32(), name, ReadOnly | 0x800, Beneath | NoLinks | NoMounts);
        if (!Inspect(file).IsFile) { file.Dispose(); throw new IOException("Unsupported source type or hard link."); }
        return new FileStream(file, FileAccess.Read);
    }

    /// <summary>Exclusively create one private destination file; existing content is never overwritten.</summary>
    public FileStream Write(string name)
    {
        ValidateName(name);
        return new FileStream(Open(handle.DangerousGetHandle().ToInt32(), name, ReadWrite | Create | Exclusive,
            Beneath | NoLinks | NoMounts, 0x180), FileAccess.ReadWrite);
    }

    /// <summary>Commit a prepared owned name atomically without replacement and flush its directory.</summary>
    public void Publish(string partial, string final)
    {
        ValidateName(partial); ValidateName(final);
        if (renameat2(handle, partial, handle, final, 1) != 0 || fsync(handle) != 0)
            throw new IOException("Destination publication failed.");
    }

    /// <summary>Remove a previously verified owned regular file relative to this directory.</summary>
    public void Delete(string name)
    {
        using (Read(name)) { }
        if (unlinkat(handle, name, 0) != 0 || fsync(handle) != 0) throw new IOException("Owned file cleanup failed.");
    }

    /// <summary>Remove an empty verified direct child on this mount, rechecking its open identity before unlink and parent flush.</summary>
    public void RemoveChild(string name, SafeDirectory child)
    {
        ValidateName(name);
        if (name.Contains('/')) throw new IOException("Direct child name required.");
        using var current = Child(name);
        if (!current.Identity.Equals(child.Identity) || current.Identity.Mount != child.Identity.Mount || child.Names().Length != 0)
            throw new IOException("Owned child changed or is not empty.");
        if (unlinkat(handle, name, 0x200) != 0 || fsync(handle) != 0) throw new IOException("Owned directory cleanup failed.");
    }

    /// <summary>Durably commit directory entry changes such as operational receipt/configuration renames.</summary>
    public void Flush()
    {
        if (fsync(handle) != 0) throw new IOException("Directory durability failed.");
    }

    /// <summary>Available bytes on this open filesystem exclude blocks reserved from ordinary writers.</summary>
    public long AvailableBytes
    {
        get
        {
            if (fstatfs(handle, out var facts) != 0 || facts.BlockSize <= 0) throw new IOException("Cannot inspect capacity.");
            return checked((long)facts.AvailableBlocks * facts.BlockSize);
        }
    }

    /// <summary>Reclaim only private verification trees, refusing links, mount crossings and excessive depth.</summary>
    public void Clear(int depth = 0)
    {
        if (depth > 33) throw new IOException("Staging cleanup depth exceeded.");
        foreach (var name in Names())
        {
            SafeDirectory? child = null;
            try { child = Child(name); }
            catch (IOException) { Delete(name); }
            if (child is null) continue;
            using (child) child.Clear(depth + 1);
            if (unlinkat(handle, name, 0x200) != 0) throw new IOException("Staging directory cleanup failed.");
        }
        Flush();
    }

    public void Dispose() => handle.Dispose();
}
