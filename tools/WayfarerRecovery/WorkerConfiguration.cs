using System.Text.Json;

namespace WayfarerRecovery;

/// <summary>Derived immutable worker input; installation.json remains the only backup policy authority.</summary>
public sealed record WorkerConfiguration
{
    public int Schema { get; init; } = 1;
    public Guid Installation { get; init; }
    public string Generation { get; init; } = "";
    public bool Enabled { get; init; }
    public string DestinationKind { get; init; } = "local";
    public uint DeviceMajor { get; init; }
    public uint DeviceMinor { get; init; }
    public ulong Inode { get; init; }
    public int Retention { get; init; } = 7;
    public int DailyMinute { get; init; } = 180;
    public int JitterMinutes { get; init; } = 15;
    public int Attempts { get; init; } = 3;
    public int DeadlineSeconds { get; init; } = 600;
    public SourceIdentity Source { get; init; } = new();

    /// <summary>Strictly read the host-generated contract, never ambient environment or another policy file.</summary>
    public static WorkerConfiguration Load(string path)
    {
        if (new FileInfo(path).Length > ArchiveContract.ManifestLimit) throw new IOException("Worker configuration too large.");
        var config = JsonSerializer.Deserialize<WorkerConfiguration>(File.ReadAllText(path), ArchiveContract.Json)
            ?? throw new IOException("Worker configuration missing.");
        if (config.Schema != 1 || config.Installation == Guid.Empty || config.Generation.Length != 64 ||
            config.Retention is < 1 or > 100 || config.DailyMinute is < 0 or >= 1440 || config.JitterMinutes is < 0 or > 15 ||
            config.Attempts is < 1 or > 3 || config.DeadlineSeconds is < 30 or > 3600 ||
            config.DestinationKind is not ("local" or "mounted") || config.Source.ExpectedMigrations.Length == 0)
            throw new IOException("Invalid worker configuration.");
        return config;
    }

    /// <summary>Validate the currently visible mount/marker; never reuse an old detached directory as authority.</summary>
    public SafeDirectory OpenDestination()
    {
        const string path = "/destination/slot";
        var directory = new SafeDirectory(path);
        try
        {
            var identity = directory.Identity;
            if (identity.DeviceMajor != DeviceMajor || identity.DeviceMinor != DeviceMinor || identity.Inode != Inode ||
                identity.User != 1654 || identity.Group != 1654 || (identity.Mode & 0x1ff) != 0x1c0)
                throw new IOException("Destination identity or permissions changed.");
            if (DestinationKind == "mounted")
            {
                using var parent = new SafeDirectory("/destination");
                if (identity.Mount == parent.Identity.Mount) throw new IOException("Remote destination is unmounted.");
            }
            using var marker = directory.Read(".wayfarer-recovery");
            if (marker.Length > 128 || new StreamReader(marker).ReadToEnd() != $"wayfarer-recovery-v1\n{Installation:D}\n")
                throw new IOException("Destination installation marker mismatch.");
            return directory;
        }
        catch { directory.Dispose(); throw; }
    }
}
