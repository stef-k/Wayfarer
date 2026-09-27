using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WayfarerRecovery;

/// <summary>Single-host Linux POSIX byte-range exclusion, independent of database availability.</summary>
public sealed class RecoveryLock : IDisposable
{
    private readonly SafeFileHandle handle;

    [StructLayout(LayoutKind.Sequential)]
    private struct Range
    {
        public short Type;
        public short Whence;
        public long Start;
        public long Length;
        public int Process;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open(string path, int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(SafeFileHandle descriptor, int command, ref Range range);

    /// <summary>Open an already-provisioned stable inode; never create, replace or unlink the lock.</summary>
    public RecoveryLock(string path)
    {
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Recovery locking requires Linux AMD64.");
        var descriptor = open(path, 2 | 0x20000 | 0x80000); // O_RDWR | O_NOFOLLOW | O_CLOEXEC.
        if (descriptor < 0) throw new IOException("Recovery lock is unavailable.");
        handle = new SafeFileHandle((IntPtr)descriptor, ownsHandle: true);
        var range = new Range { Type = 1, Length = 1 }; // F_WRLCK; SEEK_SET; byte [0,1).
        if (fcntl(handle, 6, ref range) == 0) return; // F_SETLK, nonblocking.
        handle.Dispose();
        throw new IOException("Recovery lock is busy or unavailable.");
    }

    /// <summary>Kernel ownership ends on close or process death, with no stale-file recovery.</summary>
    public void Dispose() => handle.Dispose();
}
