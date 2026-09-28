using System.Text.Json;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Versioned compatibility classification never authenticates archive bytes or authorizes SQL.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class RecoveryCompatibilityTests
{
    /// <summary>Legacy targets retain exact equality; trusted new targets opt into legacy support explicitly.</summary>
    [Fact]
    public void LegacyArchivesRequireExplicitExactReleaseSupport()
    {
        var legacy = Source(2);
        var archive = Manifest(legacy);
        Assert.True(ArchiveVerifier.IsCompatible(archive, legacy));
        Assert.False(ArchiveVerifier.IsCompatible(archive, legacy with { QuartzIdentity = "different" }));
        var target = Source(3);
        Assert.True(ArchiveVerifier.IsCompatible(archive, target));
        Assert.False(ArchiveVerifier.IsCompatible(archive, target with { SupportedLegacySourceSchemas = [] }));
        foreach (var wrong in new[]
        {
            target with { ApplicationVersion = "2.0" }, target with { SourceRevision = new string('f', 40) },
            target with { ApplicationImage = "ghcr.io/stef-k/wayfarer@sha256:" + new string('f', 64) },
            target with { DatabaseImage = "ghcr.io/stef-k/wayfarer-db@sha256:" + new string('f', 64) },
            target with { ExpectedMigrations = ["20260924220354_Other"] },
            target with { BundleFingerprint = new string('f', 64) }, target with { PayloadFingerprint = new string('f', 64) },
            target with { WorkerVersion = "2" }, target with { ReleaseStatus = "released" }
        }) Assert.False(ArchiveVerifier.IsCompatible(archive, wrong));
    }

    /// <summary>New archives compare release contract, never target snapshot; old targets cannot understand them.</summary>
    [Fact]
    public void NewArchivesSeparateReleaseCompatibilityFromCaptureEvidence()
    {
        var source = Source(3);
        var archive = Manifest(source);
        Assert.True(ArchiveVerifier.IsCompatible(archive, source with { QuartzSnapshotFingerprint = new string('f', 32) }));
        Assert.False(ArchiveVerifier.IsCompatible(archive, source with { QuartzCompatibilityContract = "wayfarer-quartz-postgres-v2" }));
        Assert.False(ArchiveVerifier.IsCompatible(archive, Source(2)));
        Assert.False(ArchiveVerifier.IsCompatible(archive with { Database = archive.Database with { Major = 16 } }, source));
        Assert.False(ArchiveVerifier.IsCompatible(archive with { Database = archive.Database with { Citext = "1.5" } }, source));
    }

    /// <summary>Both persisted generations round-trip unchanged; partial or mixed semantics fail closed.</summary>
    [Fact]
    public void VersionedSourcesRejectMixedAndMissingInterpretation()
    {
        foreach (var schema in new[] { 2, 3 })
        {
            var source = Source(schema);
            var json = JsonSerializer.Serialize(source, ArchiveContract.Json);
            var parsed = JsonSerializer.Deserialize<SourceIdentity>(json, ArchiveContract.Json)!;
            ArchiveContract.ValidateSource(parsed);
            Assert.Equal(json, JsonSerializer.Serialize(parsed, ArchiveContract.Json));
        }
        foreach (var malformed in new[]
        {
            Source(2) with { QuartzCompatibilityContract = "new" }, Source(2) with { QuartzSnapshotFingerprint = new string('a', 32) },
            Source(2) with { SupportedLegacySourceSchemas = [] }, Source(2) with { QuartzIdentity = null },
            Source(3) with { QuartzIdentity = "old" }, Source(3) with { QuartzCompatibilityContract = null },
            Source(3) with { QuartzSnapshotFingerprint = null }, Source(3) with { SupportedLegacySourceSchemas = null },
            Source(3) with { SupportedLegacySourceSchemas = [3] }, Source(3) with { ConfigurationSchema = 4 }
        })
        {
            var parsed = JsonSerializer.Deserialize<SourceIdentity>(JsonSerializer.Serialize(malformed), ArchiveContract.Json)!;
            Assert.Throws<IOException>(() => ArchiveContract.ValidateSource(parsed));
        }
    }

    /// <summary>Bounded complete identities differ only in explicitly versioned Quartz semantics.</summary>
    internal static SourceIdentity Source(int schema) => new()
    {
        ApplicationVersion = "1.9.19", SourceRevision = new string('a', 40),
        ApplicationImage = "ghcr.io/stef-k/wayfarer@sha256:" + new string('b', 64),
        DatabaseImage = "ghcr.io/stef-k/wayfarer-db@sha256:" + new string('c', 64),
        BundleFingerprint = new string('d', 64), PayloadFingerprint = new string('e', 64),
        Project = "fixture", WorkerVersion = "1", ConfigurationSchema = schema,
        QuartzIdentity = schema == 2 ? new string('a', 32) : null,
        QuartzCompatibilityContract = schema == 3 ? QuartzSchemaInstaller.RecoveryCompatibilityContract : null,
        QuartzSnapshotFingerprint = schema == 3 ? new string('b', 32) : null,
        SupportedLegacySourceSchemas = schema == 3 ? [2] : null,
        ExpectedMigrations = ["20260924220353_StablePersonalCredentialCompanion"]
    };

    /// <summary>Valid inert manifest metadata for the classification seam, without claiming verified archive bytes.</summary>
    private static RecoveryManifest Manifest(SourceIdentity source) => new()
    {
        Source = source, Installation = Guid.NewGuid(), Archive = Guid.NewGuid(),
        Started = DateTimeOffset.UnixEpoch, Completed = DateTimeOffset.UnixEpoch, Mode = "quiesced",
        Database = new DatabaseIdentity
        {
            Major = 17, ServerVersion = "17.11", Name = "wayfarer", PostgisExtension = "3.6.4", PostgisLibrary = "3.6.4",
            Citext = "1.6", Encoding = "UTF8", Collation = "C.UTF-8", CharacterType = "C.UTF-8", LocaleProvider = "c",
            DumpVersion = "17.11", RestoreVersion = "17.11", Migrations = source.ExpectedMigrations,
            TerminalMigration = source.ExpectedMigrations[^1]
        },
        Components = new[] { "database", "data-protection", "uploads" }.Select((name, i) => new RecoveryComponent(
            name, ArchiveContract.Members[i + 1], 0, new string('a', 64), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch)).ToArray()
    };
}
