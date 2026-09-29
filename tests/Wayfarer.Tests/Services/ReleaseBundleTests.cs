using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Contract tests prove retained bytes, strict parsing and the legacy exact-target bridge.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ReleaseBundleTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "wayfarer-release-" + Guid.NewGuid().ToString("N"));

    public ReleaseBundleTests()
    {
        Directory.CreateDirectory(directory);
        foreach (var path in ReleaseContract.Payloads)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(directory, path))!);
            File.WriteAllText(Path.Combine(directory, path), path);
            File.SetUnixFileMode(Path.Combine(directory, path), (UnixFileMode)ReleaseContract.Mode(path));
        }
        Save(Manifest());
    }

    [Fact]
    public void RetainedBytesReconstructExactLegacyTargetAfterDirectoryMove()
    {
        var original = ReleaseBundle.Validate(directory);
        var target = original.Target("local-project");
        Assert.Equal(Legacy("compose.yaml", "external.yaml", "caddy/Caddyfile", "db/20-wayfarer.sh"), target.BundleFingerprint);
        Assert.Equal(Legacy("wayfarer-recovery", "WayfarerRecoverySource.dll"), target.PayloadFingerprint);
        Directory.Move(directory, directory + "-moved");
        try
        {
            var retained = ReleaseBundle.Validate(directory + "-moved");
            Assert.Equal(original.Fingerprint, retained.Fingerprint);
            retained.Corroborate(target);
            Assert.Throws<IOException>(() => retained.Corroborate(target with { SourceRevision = new string('b', 40) }));
        }
        finally { Directory.Move(directory + "-moved", directory); }
    }

    [Theory]
    [InlineData("compose.yaml")]
    [InlineData("wayfarerctl")]
    [InlineData("wayfarer-recovery")]
    [InlineData("WayfarerRecoverySource.dll")]
    public void PayloadTamperingFails(string path)
    {
        File.SetUnixFileMode(Path.Combine(directory, path), (UnixFileMode)420);
        File.AppendAllText(Path.Combine(directory, path), "tampered");
        File.SetUnixFileMode(Path.Combine(directory, path), (UnixFileMode)ReleaseContract.Mode(path));
        Assert.Throws<IOException>(() => ReleaseBundle.Validate(directory));
    }

    [Fact]
    public void UnknownDuplicateAndMissingPropertiesFail()
    {
        var text = File.ReadAllText(Path.Combine(directory, "release.json"));
        foreach (var invalid in new[] { text.Insert(1, "\"Unknown\":1,"), text.Insert(1, "\"Schema\":1,"), text.Replace("\"Schema\":1,", "") })
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(invalid));
            Assert.ThrowsAny<Exception>(() => ReleaseContract.Read(stream));
        }
    }

    [Fact]
    public void UnknownVersionsAndOperatorSchemasFailClosed()
    {
        var manifest = Manifest();
        foreach (var invalid in new[] { manifest with { Schema = 2 }, manifest with { ConfigurationSchema = 2 },
            manifest with { BundleContract = 2 }, manifest with { Operator = manifest.Operator with { UpdateReceiptSchemas = [2] } },
            manifest with { Status = "candidate", Tag = "v1.9.19" }, manifest with { Status = "stable", Tag = null } })
            Assert.Throws<IOException>(() => ReleaseContract.Validate(invalid));
        ReleaseContract.RequireUse(manifest, "1.9.19");
        Assert.Throws<IOException>(() => ReleaseContract.RequireUse(manifest, "1.9.18"));
    }

    [Fact]
    public void ExtraFileLinkAndWrongModeAreRejected()
    {
        var extra = Path.Combine(directory, "extra");
        File.WriteAllText(extra, "extra");
        Assert.Throws<IOException>(() => ReleaseBundle.Validate(directory));
        File.Delete(extra);
        var path = Path.Combine(directory, "compose.yaml");
        File.Delete(path);
        File.CreateSymbolicLink(path, "/etc/passwd");
        Assert.Throws<IOException>(() => ReleaseBundle.Validate(directory));
        File.Delete(path);
        File.WriteAllText(path, "compose.yaml");
        File.SetUnixFileMode(path, (UnixFileMode)438);
        Assert.Throws<IOException>(() => ReleaseBundle.Validate(directory));
    }

    [Fact]
    public void SourceBoundaryRequiresExplicitExactSchemaAndReleaseIdentity()
    {
        var manifest = Manifest();
        var boundary = new ReleaseSourceBoundary("1.9.18", new string('e', 64), manifest.Application.TerminalMigration, true, false, "none", "");
        ReleaseContract.Validate(manifest with { Sources = [boundary] });
        Assert.Throws<IOException>(() => ReleaseContract.Validate(manifest with { Sources = [boundary with { ExactOrderedPrefix = false }] }));
        Assert.Throws<IOException>(() => ReleaseContract.Validate(manifest with { Sources = [boundary with { TerminalMigration = "unknown" }] }));
    }

    /// <summary>Retained targets support both capture schemas without selecting historical physical layout.</summary>
    [Fact]
    public void RetainedTargetBridgesLegacyAndIgnoresCaptureSnapshots()
    {
        var target = ReleaseBundle.Validate(directory).Target("fixture");
        Assert.Equal(3, target.ConfigurationSchema);
        Assert.Null(target.QuartzIdentity);
        Assert.Equal(QuartzSchemaInstaller.RecoveryCompatibilityContract, target.QuartzCompatibilityContract);
        var captured = target with { QuartzSnapshotFingerprint = new string('e', 32) };
        Assert.True(ArchiveVerifier.IsCompatible(RecoveryCompatibilityTests.Manifest(captured), target));
        var legacy = target with { ConfigurationSchema = 2, QuartzIdentity = new string('f', 32),
            QuartzCompatibilityContract = null, QuartzSnapshotFingerprint = null, SupportedLegacySourceSchemas = null };
        Assert.True(ArchiveVerifier.IsCompatible(RecoveryCompatibilityTests.Manifest(legacy), target));
        ReleaseBundle.Validate(directory).Corroborate(legacy);
        Assert.False(ArchiveVerifier.IsCompatible(RecoveryCompatibilityTests.Manifest(legacy), target with { SupportedLegacySourceSchemas = [] }));
        Assert.False(ArchiveVerifier.IsCompatible(RecoveryCompatibilityTests.Manifest(legacy), target with { SourceRevision = new string('b', 40) }));
    }

    /// <summary>Every inventory role is mandatory; traversal, aliases and duplicate entries cannot replace one.</summary>
    [Theory]
    [InlineData("../compose.yaml")]
    [InlineData("/compose.yaml")]
    [InlineData("caddy\\Caddyfile")]
    [InlineData("wayfarerctl")]
    public void InventoryRejectsAmbiguousPaths(string path)
    {
        var manifest = Manifest();
        manifest.Files[0] = manifest.Files[0] with { Path = path };
        Assert.Throws<IOException>(() => ReleaseContract.Validate(manifest));
    }

    /// <summary>Inert syntax does not prove executable compatibility; minimum protocol is independent of app SemVer.</summary>
    [Fact]
    public void InspectionAndUseHaveSeparateCompatibilityDecisions()
    {
        var manifest = Manifest() with { Operator = Manifest().Operator with { Version = "2.0.0", MinimumVersion = "2.0.0" } };
        ReleaseContract.Validate(manifest);
        Assert.Throws<IOException>(() => ReleaseContract.RequireUse(manifest, "1.9.19"));
        ReleaseContract.RequireUse(manifest, "2.0.0");
        Assert.Throws<IOException>(() => ReleaseContract.Validate(manifest with
        { Application = manifest.Application with { SupportedLegacySourceSchemas = [2, 2] } }));
    }

    /// <summary>Release adoption preserves both canonical and previously restored storage selection.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("0123456789abcdef0123456789abcdef")]
    public void SchemaFourRetainsStorageGeneration(string? generation)
    {
        var bundle = ReleaseBundle.Validate(directory);
        var config = new Deployment { Schema = 4, Release = ReleaseAuthority.From(bundle), Bundle = directory,
            Hostname = "wayfarer.example.org", AppDigest = bundle.Manifest.Images.ApplicationDigest, StorageGeneration = generation };
        config.Validate();
        var identity = Guid.NewGuid();
        var target = RestoreCommands.WithRestoreIdentity(config, identity);
        Assert.Equal(4, target.Schema);
        Assert.Equal(identity, target.Installation);
        Assert.Equal(config.Release, target.Release);
        Assert.Equal(target, RestoreCommands.WithRestoreIdentity(target, Guid.NewGuid()));
        Assert.Equal("wayfarer_db-data" + (generation is null ? "" : "_" + generation), ActiveStorage.Volume(config, "db-data"));
        Assert.Throws<UsageException>(() => (config with { Schema = 1 }).Validate());
    }

    /// <summary>Historical helper bytes remain an explicit retained profile, never copied into the new capture identity.</summary>
    [Fact]
    public void HistoricalCapturePairIsInventoriedAndCorroboratedWithoutQuartzHistory()
    {
        Directory.CreateDirectory(Path.Combine(directory, "capture"));
        var files = Manifest().Files.ToList();
        foreach (var path in ReleaseContract.CapturePayloads)
        {
            File.WriteAllText(Path.Combine(directory, path), "old:" + path);
            File.SetUnixFileMode(Path.Combine(directory, path), (UnixFileMode)ReleaseContract.Mode(path));
            files.Add(new(path, Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, path)))), "file", ReleaseContract.Mode(path)));
        }
        Save(Manifest() with { LegacyCapture = new("1.9.18.0", "candidate"), Files = files.ToArray() });
        var bundle = ReleaseBundle.Validate(directory);
        var historical = bundle.Target("fixture");
        var current = bundle.Target("fixture", true);
        Assert.NotEqual(current.PayloadFingerprint, historical.PayloadFingerprint);
        Assert.Equal("1.9.18.0", historical.WorkerVersion);
        bundle.Corroborate(historical);
        bundle.Corroborate(current);
        var legacy = historical with { ConfigurationSchema = 2, QuartzIdentity = new string('f', 32),
            QuartzCompatibilityContract = null, QuartzSnapshotFingerprint = null, SupportedLegacySourceSchemas = null };
        Assert.True(ArchiveVerifier.IsCompatible(RecoveryCompatibilityTests.Manifest(legacy), historical));
        Assert.False(ArchiveVerifier.IsCompatible(RecoveryCompatibilityTests.Manifest(legacy), current));
    }

    /// <summary>A later version alone cannot grant update authority; candidate qualification still demands an exact source prefix.</summary>
    [Fact]
    public void UpdateRequiresExplicitSourceAndExactPrefixEvenForCandidates()
    {
        var source = ReleaseBundle.Validate(directory);
        var manifest = source.Manifest;
        var boundary = new ReleaseSourceBoundary(manifest.Version, source.Fingerprint,
            manifest.Application.TerminalMigration, true, false, "manual-recovery", "candidate qualification");
        var target = source with { Manifest = manifest with
        {
            Version = "1.9.20", Operator = ReleaseContract.CurrentOperator, Sources = [boundary],
            Images = manifest.Images with { ApplicationDigest = "sha256:" + new string('e', 64) },
            Application = manifest.Application with { Migrations = [.. manifest.Application.Migrations, "20260929000000_Forward"] }
        } };
        Assert.Throws<UsageException>(() => UpdateOptions.Boundary(source, target));
        Assert.Equal(boundary, UpdateOptions.Boundary(source, target, candidates: true));
        Assert.Throws<UsageException>(() => UpdateOptions.Boundary(source,
            target with { Manifest = target.Manifest with { Sources = [boundary with { Fingerprint = new string('f', 64) }] } }, true));
        Assert.Throws<UsageException>(() => UpdateOptions.Boundary(source,
            target with { Manifest = target.Manifest with { Application = target.Manifest.Application with { Migrations = ["20260929000000_Forward"] } } }, true));
        Assert.Throws<UsageException>(() => UpdateOptions.Boundary(source,
            target with { Manifest = target.Manifest with { Sources = [boundary with { ReferenceSeeding = true }] } }, true));
    }

    private ReleaseManifest Manifest() => new(1, 1, 1, "candidate", "1.9.19", null,
        "https://github.com/stef-k/Wayfarer", new string('a', 40), "linux/amd64",
        new("ghcr.io/stef-k/wayfarer", "sha256:" + new string('b', 64), "sha256:" + new string('b', 64), "1.9.19", ReleaseContract.DatabaseDigest,
            ReleaseContract.CaddyDigest, 17, "3.6.4", "1.6", "UTF8", "C.UTF-8", "C.UTF-8", "c"),
        new("1.9.19", ["20260101000000_Initial"], "20260101000000_Initial", "wayfarer-quartz-postgres-v1", [2], new string('d', 64),
            "Wayfarer", "uploads", "data-protection", "ready", "1.9.19.0"),
        new("1.9.19", "1.9.19", 1, [1], [1, 2, 3, 4], [1], [1], []), [],
        ReleaseContract.Payloads.Select(path => new ReleaseFile(path,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, path)))), "file", ReleaseContract.Mode(path))).ToArray(), null);

    private void Save(ReleaseManifest manifest)
    {
        var path = Path.Combine(directory, "release.json");
        File.WriteAllText(path, JsonSerializer.Serialize(manifest));
        File.SetUnixFileMode(path, (UnixFileMode)420);
    }

    private string Legacy(params string[] paths)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var path in paths) hash.AppendData(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, path))));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    public void Dispose() => Directory.Delete(directory, true);
}
