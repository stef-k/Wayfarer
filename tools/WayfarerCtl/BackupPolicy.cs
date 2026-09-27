using System.Security.Cryptography;
using System.Text.RegularExpressions;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Installation-owned backup policy and immutable additive worker payload identity.</summary>
public sealed record BackupPolicy
{
    public bool Enabled { get; init; } = true;
    public string Destination { get; init; } = "";
    public string Kind { get; init; } = "local";
    public string Payload { get; init; } = "";
    public string PayloadSha256 { get; init; } = "";
    public string Generation { get; init; } = "";
    public uint DeviceMajor { get; init; }
    public uint DeviceMinor { get; init; }
    public ulong Inode { get; init; }
    public int Retention { get; init; } = 7;
    public int DailyMinute { get; init; } = 180;
    public int JitterMinutes { get; init; } = 15;
    public int Attempts { get; init; } = 3;
    public int DeadlineSeconds { get; init; } = 600;
    public SourceIdentity Source { get; init; } = new();
    public string Uploads { get; init; } = "";
    public string Ring { get; init; } = "";

    /// <summary>Reject ambiguous paths, unsupported policy and non-immutable payload identities.</summary>
    public void Validate()
    {
        LiteralPath(Destination); LiteralPath(Payload);
        if (Kind is not ("local" or "mounted") || Retention is < 1 or > 100 || DailyMinute is < 0 or >= 1440 ||
            JitterMinutes is < 0 or > 15 || Attempts is < 1 or > 3 || DeadlineSeconds is < 30 or > 3600 ||
            !Regex.IsMatch(PayloadSha256, "^[a-f0-9]{64}$") || !Regex.IsMatch(Generation, "^[a-f0-9]{64}$"))
            throw new UsageException("Invalid backup policy.");
        SafeDirectory.ValidateName(Uploads); SafeDirectory.ValidateName(Ring);
    }

    /// <summary>Literal input cannot expand Compose variables or escape a configured authority.</summary>
    public static void LiteralPath(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path == "/" || path.Length > 1024 ||
            path.Any(char.IsControl) || path.IndexOfAny(['$', '\\', '"', '\'', '`']) >= 0 ||
            Path.GetFullPath(path) != path || path.EndsWith('/')) throw new UsageException("A canonical literal absolute path is required.");
    }

    /// <summary>Only a root-owned immutable executable with its persisted checksum may run.</summary>
    public void CheckPayload()
    {
        ProtectedFiles.SafePath(Payload);
        using var parent = new SafeDirectory(Path.GetDirectoryName(Payload)!);
        using var file = parent.Read(Path.GetFileName(Payload));
        var facts = SafeDirectory.Inspect(file.SafeFileHandle);
        if (facts.User != 0 || (facts.Mode & 0x92) != 0 || (facts.Mode & 0x40) == 0 ||
            Fingerprint(Payload) != PayloadSha256)
            throw new UsageException("Backup payload identity or immutable ownership changed.");
    }

    /// <summary>Bind both additive executables, without changing the application or original bundle.</summary>
    public static string Fingerprint(string payload)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in new[] { payload, Path.Combine(Path.GetDirectoryName(payload)!, "WayfarerRecoverySource.dll") })
        {
            ProtectedFiles.SafePath(path);
            hash.AppendData(SHA256.HashData(File.ReadAllBytes(path)));
        }
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>Generated worker input is derived exclusively from the installation generation.</summary>
    public WorkerConfiguration Worker(Guid installation) => new()
    {
        Installation = installation, Generation = Generation, Enabled = Enabled, DestinationKind = Kind,
        DeviceMajor = DeviceMajor, DeviceMinor = DeviceMinor, Inode = Inode, Retention = Retention,
        DailyMinute = DailyMinute, JitterMinutes = JitterMinutes, Attempts = Attempts,
        DeadlineSeconds = DeadlineSeconds, Source = Source, Uploads = Uploads, Ring = Ring
    };
}
