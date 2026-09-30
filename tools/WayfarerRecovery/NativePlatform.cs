using System.Runtime.InteropServices;

namespace WayfarerRecovery;

/// <summary>The two supported native Linux ABIs share one operator and recovery implementation.</summary>
public static class NativePlatform
{
    /// <summary>Reject emulated processes and unsupported hosts before native filesystem or lifecycle access.</summary>
    public static string Current
    {
        get
        {
            if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture)
                throw new PlatformNotSupportedException("A native Linux AMD64 or ARM64 process is required.");
            return Name(RuntimeInformation.ProcessArchitecture);
        }
    }

    /// <summary>Architecture is a closed release dimension, never an arbitrary user-supplied platform.</summary>
    public static string Name(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "linux/amd64",
        Architecture.Arm64 => "linux/arm64",
        _ => throw new PlatformNotSupportedException("Supported Linux architectures: AMD64 and ARM64.")
    };

    /// <summary>Linux UAPI open flags differ on AArch64; syscall and filesystem safety semantics stay shared.</summary>
    public static (int Directory, int NoFollow) OpenFlags(Architecture architecture) => architecture switch
    {
        Architecture.X64 => (0x10000, 0x20000),
        Architecture.Arm64 => (0x4000, 0x8000),
        _ => throw new PlatformNotSupportedException("Unsupported Linux open ABI.")
    };

    /// <summary>Metadata inspection accepts only the two supported release platforms.</summary>
    public static bool Supported(string platform) => platform is "linux/amd64" or "linux/arm64";
}
