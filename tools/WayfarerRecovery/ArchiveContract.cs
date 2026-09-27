using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace WayfarerRecovery;

/// <summary>Versioned interpretation and limits shared by capture, listing and verification.</summary>
public static class ArchiveContract
{
    public const int ManifestLimit = 131072;
    public const int EntryLimit = 100000;
    public const long ByteLimit = 100L * 1024 * 1024 * 1024;
    public static readonly string[] Members = ["manifest.json", "database.dump", "data-protection.tar.gz", "uploads.tar.gz", "SHA256SUMS"];
    public static readonly JsonSerializerOptions Json = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        WriteIndented = false
    };

    /// <summary>Only generated identities are permitted in filesystem names.</summary>
    public static string Name(Guid installation, DateTimeOffset completion, Guid archive) =>
        $"wayfarer-recovery-v1_{installation:D}_{completion.UtcDateTime:yyyyMMddTHHmmssfffffffZ}_{archive:D}.tar";

    /// <summary>Validate bounded identity fields without interpreting any manifest value as a command or path.</summary>
    public static void Validate(RecoveryManifest manifest)
    {
        if (manifest.Format != "wayfarer-recovery" || manifest.Version != 1 || manifest.Schema != 1 ||
            manifest.Algorithm != "SHA-256" || manifest.Installation == Guid.Empty || manifest.Archive == Guid.Empty ||
            manifest.Completed < manifest.Started || manifest.Mode is not ("online" or "quiesced") ||
            manifest.Source.Kind is not ("compose" or "native") || manifest.Source.ApplicationName != "Wayfarer" ||
            manifest.Components.Length != 3) throw new IOException("Unsupported recovery manifest.");
        for (var i = 0; i < 3; i++)
        {
            var component = manifest.Components[i];
            if (component.Member != Members[i + 1] || component.Name != new[] { "database", "data-protection", "uploads" }[i] ||
                component.Length is < 0 or > ByteLimit || !Regex.IsMatch(component.Sha256, "^[a-f0-9]{64}$") ||
                component.Completed < component.Started || component.Started < manifest.Started || component.Completed > manifest.Completed)
                throw new IOException("Invalid recovery component.");
        }
    }
}

/// <summary>Complete recovery-set metadata; source provenance never supplies extraction paths.</summary>
public sealed record RecoveryManifest
{
    [JsonRequired]
    public string Format { get; init; } = "wayfarer-recovery";
    [JsonRequired]
    public int Version { get; init; } = 1;
    [JsonRequired]
    public int Schema { get; init; } = 1;
    [JsonRequired]
    public string Algorithm { get; init; } = "SHA-256";
    [JsonRequired]
    public Guid Installation { get; init; }
    [JsonRequired]
    public Guid Archive { get; init; }
    [JsonRequired]
    public DateTimeOffset Started { get; init; }
    [JsonRequired]
    public DateTimeOffset Completed { get; init; }
    [JsonRequired]
    public string Mode { get; init; } = "online";
    public DateTimeOffset? ScheduledSlot { get; init; }
    [JsonRequired]
    public SourceIdentity Source { get; init; } = new();
    [JsonRequired]
    public DatabaseIdentity Database { get; init; } = new();
    [JsonRequired]
    public RecoveryComponent[] Components { get; init; } = [];
}

/// <summary>Compatibility identity supplied by trusted installation and application owners.</summary>
public sealed record SourceIdentity
{
    [JsonRequired]
    public string Kind { get; init; } = "compose";
    [JsonRequired]
    public string ApplicationName { get; init; } = "Wayfarer";
    [JsonRequired]
    public string ApplicationVersion { get; init; } = "";
    [JsonRequired]
    public string SourceRevision { get; init; } = "";
    [JsonRequired]
    public string ReleaseStatus { get; init; } = "candidate";
    [JsonRequired]
    public string ApplicationImage { get; init; } = "";
    [JsonRequired]
    public string DatabaseImage { get; init; } = "";
    [JsonRequired]
    public string Platform { get; init; } = "linux/amd64";
    [JsonRequired]
    public string BundleFingerprint { get; init; } = "";
    [JsonRequired]
    public string PayloadFingerprint { get; init; } = "";
    [JsonRequired]
    public string Project { get; init; } = "";
    [JsonRequired]
    public string WorkerVersion { get; init; } = "";
    [JsonRequired]
    public int ConfigurationSchema { get; init; } = 2;
    [JsonRequired]
    public string StableIdentity { get; init; } = "ready";
    [JsonRequired]
    public string[] ExpectedMigrations { get; init; } = [];
    [JsonRequired]
    public string QuartzIdentity { get; init; } = "";
}

/// <summary>Database identity observed from the dump's exported read-only snapshot.</summary>
public sealed record DatabaseIdentity
{
    [JsonRequired]
    public int Major { get; init; }
    [JsonRequired]
    public string ServerVersion { get; init; } = "";
    [JsonRequired]
    public string Name { get; init; } = "";
    [JsonRequired]
    public string PostgisExtension { get; init; } = "";
    [JsonRequired]
    public string PostgisLibrary { get; init; } = "";
    [JsonRequired]
    public string Citext { get; init; } = "";
    [JsonRequired]
    public string Encoding { get; init; } = "";
    [JsonRequired]
    public string Collation { get; init; } = "";
    [JsonRequired]
    public string CharacterType { get; init; } = "";
    [JsonRequired]
    public string LocaleProvider { get; init; } = "";
    [JsonRequired]
    public string? Locale { get; init; }
    [JsonRequired]
    public string DumpVersion { get; init; } = "";
    [JsonRequired]
    public string RestoreVersion { get; init; } = "";
    [JsonRequired]
    public string[] Migrations { get; init; } = [];
    [JsonRequired]
    public string TerminalMigration { get; init; } = "";
}

/// <summary>Fixed logical component identity and bounded integrity facts, without user filenames.</summary>
public sealed record RecoveryComponent(string Name, string Member, long Length, string Sha256,
    DateTimeOffset Started, DateTimeOffset Completed);
