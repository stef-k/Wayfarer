using System.Text.Json;
using System.Text.RegularExpressions;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Offline release authority; all strings are inert metadata, never executable paths or hooks.</summary>
public sealed record ReleaseManifest(int Schema, int BundleContract, int ConfigurationSchema,
    string Status, string Version, string? Tag, string Repository, string SourceRevision, string Platform,
    ReleaseImages Images, ReleaseApplication Application, ReleaseOperator Operator,
    ReleaseSourceBoundary[] Sources, ReleaseFile[] Files, ReleaseCapture? LegacyCapture)
{
    /// <summary>Candidate names cannot collide with the stable publication namespace.</summary>
    public string Name => Status == "stable" ? "v" + Version : "candidate-v" + Version + "-" + SourceRevision;
}

/// <summary>Optional independently retained historical capture pair; no installation or Quartz snapshot facts.</summary>
public sealed record ReleaseCapture(string WorkerVersion, string ReleaseStatus);

/// <summary>Index and selected platform digest are distinct even when a single-platform artifact uses the same value.</summary>
public sealed record ReleaseImages(string ApplicationRepository, string ApplicationDigest, string PlatformDigest, string OciVersion,
    string DatabaseDigest, string CaddyDigest, int PostgreSqlMajor, string Postgis, string Citext,
    string Encoding, string Collation, string CharacterType, string LocaleProvider);

/// <summary>Application-owned schema and durable-layout facts needed by exact-target restore.</summary>
public sealed record ReleaseApplication(string CompiledVersion, string[] Migrations, string TerminalMigration,
    string QuartzCompatibilityContract, int[] SupportedLegacySourceSchemas, string QuartzSha256, string DataProtectionName, string Uploads, string Ring,
    string ProtectedCredentialRequirement, string WorkerVersion);

/// <summary>Independent operator protocol versions; application version ordering grants no compatibility.</summary>
public sealed record ReleaseOperator(string Version, string MinimumVersion, int Contract,
    int[] ManifestSchemas, int[] InstallationSchemas, int[] ArchiveSchemas, int[] RestoreReceiptSchemas,
    int[] UpdateReceiptSchemas);

/// <summary>Explicit forward source boundary; an empty set means no supported update source.</summary>
public sealed record ReleaseSourceBoundary(string Version, string Fingerprint, string TerminalMigration,
    bool ExactOrderedPrefix, bool ReferenceSeeding, string RetryRestriction, string Warning);

/// <summary>Fixed regular payload inventory with numeric Unix mode and content digest.</summary>
public sealed record ReleaseFile(string Path, string Sha256, string Type, int Mode);

/// <summary>Strict v1 syntax shared by inspection, placement, adoption and dispatch.</summary>
public static class ReleaseContract
{
    public const string DatabaseDigest = "sha256:bd9b3bbfe1e879b56b0742646c18d0dcc9ec95180095f8f6d02e03b54feeeb61";
    public const string CaddyDigest = "sha256:6aeddd44c3078b0f9a35206472a11420648a79c184603ef95957d0a20044cb2b";
    public static readonly string[] Payloads = ["compose.yaml", "external.yaml", "caddy/Caddyfile", "db/20-wayfarer.sh",
        "config/deployment.env.example", "compose.sh", "INSTALL.md", "wayfarerctl", "wayfarer-recovery", "WayfarerRecoverySource.dll"];
    public static readonly string[] CapturePayloads = ["capture/wayfarer-recovery", "capture/WayfarerRecoverySource.dll"];
    public static readonly string[] Directories = ["caddy", "db", "config"];

    /// <summary>Only the explicitly versioned historical capture pair may extend the fixed inventory.</summary>
    public static string[] Inventory(ReleaseManifest manifest) => manifest.LegacyCapture is null ? Payloads : [.. Payloads, .. CapturePayloads];
    public static string[] Folders(ReleaseManifest manifest) => manifest.LegacyCapture is null ? Directories : [.. Directories, "capture"];

    /// <summary>Independent protocol declaration emitted by the exact bundled operator.</summary>
    public static ReleaseOperator CurrentOperator => new(ReleaseCommands.OperatorVersion, "1.9.19", 1,
        [1], [1, 2, 3, 4], [1], [1], [1]);

    /// <summary>Reject duplicate JSON keys before strict required-constructor deserialization.</summary>
    public static ReleaseManifest Read(Stream stream)
    {
        if (stream.Length > ArchiveContract.ManifestLimit) throw new IOException("Release manifest exceeds bound.");
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 16 });
        Unique(document.RootElement);
        var manifest = document.RootElement.Deserialize<ReleaseManifest>(ArchiveContract.Json)
            ?? throw new IOException("Missing release manifest.");
        Validate(manifest);
        return manifest;
    }

    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IOException("Duplicate release property.");
                Unique(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var value in element.EnumerateArray()) Unique(value);
    }

    /// <summary>Only the implemented protocol and accepted immutable dependency contracts may become authority.</summary>
    public static void Validate(ReleaseManifest value)
    {
        if (value.Schema != 1 || value.BundleContract != 1 || value.ConfigurationSchema != 1 ||
            value.Status is not ("candidate" or "stable") || !VersionSyntax(value.Version) ||
            value.Tag != (value.Status == "stable" ? "v" + value.Version : null) ||
            value.Repository != "https://github.com/stef-k/Wayfarer" || !Match(value.SourceRevision, "[a-f0-9]{40}") ||
            !NativePlatform.Supported(value.Platform)) throw new IOException("Unsupported release identity.");
        var images = value.Images;
        if (images.ApplicationRepository != "ghcr.io/stef-k/wayfarer" || !Match(images.ApplicationDigest, "sha256:[a-f0-9]{64}") || !Match(images.PlatformDigest, "sha256:[a-f0-9]{64}") ||
            images.OciVersion != value.Version || !Match(images.DatabaseDigest, "sha256:[a-f0-9]{64}") || images.CaddyDigest != CaddyDigest ||
            images.PostgreSqlMajor != 17 || images.Postgis != "3.6.4" || images.Citext != "1.6" || images.Encoding != "UTF8" ||
            images.Collation != "C.UTF-8" || images.CharacterType != "C.UTF-8" || images.LocaleProvider != "c")
            throw new IOException("Unsupported release image contract.");
        var app = value.Application;
        if (app.CompiledVersion != value.Version || app.DataProtectionName != "Wayfarer" || app.Uploads != "uploads" ||
            app.Ring != "data-protection" || app.ProtectedCredentialRequirement != "ready" ||
            !Match(app.QuartzCompatibilityContract, "[A-Za-z0-9.-]{1,128}") ||
            app.SupportedLegacySourceSchemas.Length > 1 || app.SupportedLegacySourceSchemas.Any(schema => schema != 2) || !Hash(app.QuartzSha256) ||
            !Match(app.WorkerVersion, "[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+") || app.Migrations.Length is 0 or > 1000 ||
            app.Migrations.Any(migration => !Match(migration, "[0-9]{14}_[A-Za-z0-9_]{1,160}")) ||
            !app.Migrations.SequenceEqual(app.Migrations.Distinct().Order(StringComparer.Ordinal)) ||
            app.TerminalMigration != app.Migrations[^1]) throw new IOException("Unsupported application compatibility.");
        if (value.LegacyCapture is { } capture && (!Match(capture.WorkerVersion, "[0-9]+\\.[0-9]+\\.[0-9]+\\.[0-9]+") ||
            capture.ReleaseStatus is not ("candidate" or "released"))) throw new IOException("Unsupported legacy capture authority.");
        ValidateOperator(value.Operator);
        if (value.Sources.Length > 100 || value.Sources.Select(source => source.Fingerprint).Distinct().Count() != value.Sources.Length)
            throw new IOException("Ambiguous source boundaries.");
        foreach (var source in value.Sources)
            if (!VersionSyntax(source.Version) || !Hash(source.Fingerprint) || !app.Migrations.Contains(source.TerminalMigration) ||
                !source.ExactOrderedPrefix || source.RetryRestriction is not ("none" or "manual-recovery") ||
                source.Warning.Length > 1024 || source.Warning.Any(char.IsControl)) throw new IOException("Unsupported source boundary.");
        if (!value.Files.Select(file => file.Path).Order(StringComparer.Ordinal).SequenceEqual(Inventory(value).Order(StringComparer.Ordinal)))
            throw new IOException("Release inventory differs from contract.");
        foreach (var file in value.Files)
            if (!Hash(file.Sha256) || file.Type != "file" || file.Mode != Mode(file.Path))
                throw new IOException("Invalid payload digest, type or mode.");
    }

    private static void ValidateOperator(ReleaseOperator value)
    {
        if (!VersionSyntax(value.Version) || !VersionSyntax(value.MinimumVersion) || value.Contract != 1 ||
            System.Version.Parse(value.MinimumVersion) > System.Version.Parse(value.Version) ||
            !value.ManifestSchemas.SequenceEqual(new[] { 1 }) || !value.InstallationSchemas.SequenceEqual(new[] { 1, 2, 3, 4 }) ||
            !value.ArchiveSchemas.SequenceEqual(new[] { 1 }) || !value.RestoreReceiptSchemas.SequenceEqual(new[] { 1 }) ||
            !value.UpdateReceiptSchemas.SequenceEqual(Array.Empty<int>()) && !value.UpdateReceiptSchemas.SequenceEqual(new[] { 1 })) throw new IOException("Unsupported operator contract.");
    }

    /// <summary>Inspect permission is distinct from activation and exact receipt ownership.</summary>
    public static void RequireUse(ReleaseManifest manifest, string operatorVersion)
    {
        if (manifest.Platform != NativePlatform.Current) throw new IOException("Release platform differs from the native operator/host.");
        if (!VersionSyntax(operatorVersion) || System.Version.Parse(operatorVersion) < System.Version.Parse(manifest.Operator.MinimumVersion))
            throw new IOException("Operator cannot use this release.");
    }

    public static int Mode(string path) => path switch
    {
        "wayfarerctl" or "wayfarer-recovery" or "capture/wayfarer-recovery" => 365,
        "WayfarerRecoverySource.dll" or "capture/WayfarerRecoverySource.dll" => 292,
        "compose.sh" or "db/20-wayfarer.sh" => 493,
        _ => 420
    };
    public static bool Hash(string value) => Match(value, "[a-f0-9]{64}");
    public static bool VersionSyntax(string value) => Match(value, "(0|[1-9][0-9]{0,5})\\.(0|[1-9][0-9]{0,5})\\.(0|[1-9][0-9]{0,5})");
    private static bool Match(string value, string pattern) => Regex.IsMatch(value, "\\A(?:" + pattern + ")\\z");
}
