using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WayfarerCtl;
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
        File.AppendAllText(Path.Combine(directory, path), "tampered");
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
            manifest with { BundleContract = 2 }, manifest with { Operator = manifest.Operator with { UpdateReceiptSchemas = [1] } },
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

    private ReleaseManifest Manifest() => new(1, 1, 1, "candidate", "1.9.19", null,
        "https://github.com/stef-k/Wayfarer", new string('a', 40), "linux/amd64",
        new("sha256:" + new string('b', 64), "sha256:" + new string('b', 64), "1.9.19", ReleaseContract.DatabaseDigest,
            ReleaseContract.CaddyDigest, 17, "3.6.4", "1.6", "UTF8", "C.UTF-8", "C.UTF-8", "c"),
        new("1.9.19", ["20260101000000_Initial"], "20260101000000_Initial", "wayfarer-quartz-postgres-v1", [2], new string('d', 64),
            "Wayfarer", "uploads", "data-protection", "ready", "1.9.19.0"),
        new("1.9.19", "1.9.19", 1, [1], [1, 2, 3, 4], [1], [1], []), [],
        ReleaseContract.Payloads.Select(path => new ReleaseFile(path,
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, path)))), "file", ReleaseContract.Mode(path))).ToArray());

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
