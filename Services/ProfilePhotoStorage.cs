using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Wayfarer.Services;

/// <summary>Private Linux file custody for generated derivatives beneath durable Uploads only.</summary>
public sealed class ProfilePhotoStorage(StoragePaths paths)
{
    /// <summary>Dedicated durable namespace; originals and client filenames never enter it.</summary>
    public string Root => Path.Combine(paths.Uploads, "profile-photos");
    // Linux uses different directory/no-follow values on the two supported ABIs.
    private static int DirectoryFlag => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x4000 : 0x10000;
    private static int NoFollow => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? 0x8000 : 0x20000;
    private const int CloseOnExec = 0x80000;

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow { public ulong Flags; public ulong Mode; public ulong Resolve; }
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Facts
    {
        [FieldOffset(16)] public uint Links;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(40)] public ulong Size;
    }
    [DllImport("libc", SetLastError = true)]
    private static extern long syscall(long number, int directory, string path, ref OpenHow how, nuint size);
    [DllImport("libc", SetLastError = true)]
    private static extern int mkdirat(SafeFileHandle directory, string name, uint mode);
    [DllImport("libc", SetLastError = true)]
    private static extern int statx(SafeFileHandle descriptor, string path, int flags, uint mask, out Facts facts);
    [DllImport("libc", SetLastError = true)]
    private static extern int fchmod(SafeFileHandle descriptor, uint mode);
    [DllImport("libc", SetLastError = true)]
    private static extern int renameat2(SafeFileHandle source, string name, SafeFileHandle target, string destination, uint flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int unlinkat(SafeFileHandle directory, string name, int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int fsync(SafeFileHandle directory);

    /// <summary>No symlink ancestor is followed. Missing configured data roots fail closed.</summary>
    private SafeFileHandle OpenRoot()
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new IOException("Photo storage is unavailable.");
        using var data = Open(-100, paths.DataRoot, DirectoryFlag);
        using var uploads = ChildDirectory(data, "uploads");
        return ChildDirectory(uploads, "profile-photos");
    }

    /// <summary>Only the service-owned namespace is created, with private permissions.</summary>
    private static SafeFileHandle ChildDirectory(SafeFileHandle parent, string name)
    {
        if (mkdirat(parent, name, 0x1c0) != 0 && Marshal.GetLastPInvokeError() != 17)
            throw new IOException("Photo directory creation failed.");
        var handle = Open(parent.DangerousGetHandle().ToInt32(), name, DirectoryFlag);
        if (name == "profile-photos" && fchmod(handle, 0x1c0) != 0)
        {
            handle.Dispose();
            throw new IOException("Photo directory permissions failed.");
        }
        return handle;
    }

    /// <summary>openat2 has the same syscall number on the supported Linux architectures.</summary>
    private static SafeFileHandle Open(int parent, string name, int flags, ulong mode = 0)
    {
        var how = new OpenHow { Flags = (ulong)(flags | NoFollow | CloseOnExec | 0x800), Mode = mode, Resolve = 4 };
        var descriptor = syscall(437, parent, name, ref how, (nuint)Marshal.SizeOf<OpenHow>());
        if (descriptor < 0) throw new IOException("Photo file access failed.");
        return new SafeFileHandle((IntPtr)descriptor, true);
    }

    /// <summary>Reject links and nonregular inodes after open, before reading or writing.</summary>
    private static Facts Inspect(SafeFileHandle handle)
    {
        if (statx(handle, "", 0x1000, 0x7ff, out var facts) != 0 || (facts.Mode & 0xf000) != 0x8000 || facts.Links != 1)
            throw new IOException("Photo file is not a private regular file.");
        return facts;
    }

    /// <summary>Flush a fresh candidate and rename without overwrite before the database pointer commits.</summary>
    internal async Task PublishAsync(Guid version, byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length is 0 or > 256 * 1024) throw new IOException("Invalid photo derivative.");
        using var root = OpenRoot();
        var temporary = Name(version, ".tmp");
        using (var file = Open(root.DangerousGetHandle().ToInt32(), temporary, 1 | 0x40 | 0x80, 0x180))
        {
            _ = Inspect(file);
            using var stream = new FileStream(file, FileAccess.Write);
            await stream.WriteAsync(bytes, ct);
            await stream.FlushAsync(ct);
            ct.ThrowIfCancellationRequested();
            stream.Flush(flushToDisk: true);
        }
        ct.ThrowIfCancellationRequested();
        if (renameat2(root, temporary, root, Name(version, ".webp"), 1) != 0 || fsync(root) != 0)
            throw new IOException("Photo publication failed.");
    }

    /// <summary>Read bounded completed bytes only after the caller has freshly checked persisted authority.</summary>
    internal byte[]? Read(Guid version)
    {
        using var root = OpenRoot();
        var name = Name(version, ".webp");
        SafeFileHandle handle;
        try { handle = Open(root.DangerousGetHandle().ToInt32(), name, 0); }
        catch (IOException) when (Marshal.GetLastPInvokeError() == 2) { return null; }
        using (handle)
        {
            var facts = Inspect(handle);
            if (facts.Size is 0 or > 256 * 1024) throw new IOException("Invalid photo file length.");
            using var stream = new FileStream(handle, FileAccess.Read);
            var bytes = new byte[(int)facts.Size];
            stream.ReadExactly(bytes);
            return bytes;
        }
    }

    /// <summary>Unknown entries remain evidence; successive bounded batches cover one complete directory pass.</summary>
    internal IEnumerable<string[]> Batches()
    {
        using var root = OpenRoot();
        return Directory.EnumerateFileSystemEntries(Root).Select(Path.GetFileName)
            .Where(name => name != null).Cast<string>().Chunk(128);
    }

    /// <summary>Unlink only canonical unreferenced regular files; a missing file is already converged.</summary>
    internal void Delete(string name)
    {
        if (!TryVersion(name, out _)) throw new IOException("Unexpected photo entry.");
        using var root = OpenRoot();
        SafeFileHandle handle;
        try { handle = Open(root.DangerousGetHandle().ToInt32(), name, 0); }
        catch (IOException) when (Marshal.GetLastPInvokeError() == 2) { return; }
        using (handle) _ = Inspect(handle);
        if (unlinkat(root, name, 0) != 0 && Marshal.GetLastPInvokeError() != 2)
            throw new IOException("Photo unlink failed.");
    }

    /// <summary>Only lowercase immutable UUID names belong to this namespace.</summary>
    internal static bool TryVersion(string name, out Guid version)
    {
        version = default;
        return name.Length is 36 or 37 && (name.EndsWith(".tmp", StringComparison.Ordinal) || name.EndsWith(".webp", StringComparison.Ordinal))
            && Guid.TryParseExact(name[..32], "N", out version) && name[..32] == version.ToString("N");
    }

    /// <summary>Retain lexical path authority as well as handle-relative kernel enforcement.</summary>
    private string Name(Guid version, string extension) => Path.GetFileName(StoragePaths.ResolveFile(Root, version.ToString("N") + extension));
}
