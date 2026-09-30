using System.Formats.Tar;
using System.IO.Compression;
using Wayfarer.Tests.Infrastructure;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Recovery product contracts at the lowest filesystem/archive seam, without container orchestration mocks.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class WayfarerRecoveryTests
{
    /// <summary>Only the supported ABIs receive their kernel-defined directory/no-follow flags.</summary>
    [Fact]
    public void NativeOpenFlagsPreserveBothSupportedAbis()
    {
        Assert.Equal((0x10000, 0x20000), NativePlatform.OpenFlags(System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal((0x4000, 0x8000), NativePlatform.OpenFlags(System.Runtime.InteropServices.Architecture.Arm64));
        Assert.Throws<PlatformNotSupportedException>(() => NativePlatform.OpenFlags(System.Runtime.InteropServices.Architecture.Arm));
    }

    /// <summary>The shipped extractor restores imports and empty directories with normalized private modes.</summary>
    [Fact]
    public void RestoreExtractionPreservesCompleteTreeWithPrivatePermissions()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TestDirectory();
        var source = Path.Combine(fixture.Path, "source");
        Directory.CreateDirectory(Path.Combine(source, "imports"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        File.WriteAllBytes(Path.Combine(source, "imports", "upload"), [0, 255, 3, 4]);
        var archive = Path.Combine(fixture.Path, "uploads.tar.gz");
        new DirectoryCapture().Capture(source, archive, "uploads", CancellationToken.None);
        var target = Path.Combine(fixture.Path, "target");
        Directory.CreateDirectory(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ArchiveVerifier.ValidateDirectory(archive, target, CancellationToken.None);
        Assert.Equal(new byte[] { 0, 255, 3, 4 }, File.ReadAllBytes(Path.Combine(target, "imports", "upload")));
        Assert.True(Directory.Exists(Path.Combine(target, "empty")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Path.Combine(target, "imports", "upload")));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(target, "empty")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("a//b")]
    [InlineData("a/./b")]
    [InlineData("C:\\file")]
    [InlineData("a\nb")]
    public void UnsafeArchiveNamesAreRejected(string name)
    {
        if (!OperatingSystem.IsLinux()) return;
        Assert.Throws<IOException>(() => SafeDirectory.ValidateName(name));
    }

    [Fact]
    public void CapturePreservesBinaryImportBytesAndEmptyDirectories()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TestDirectory();
        var source = Path.Combine(fixture.Path, "uploads");
        Directory.CreateDirectory(Path.Combine(source, "imports"));
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        var content = Enumerable.Range(0, 300000).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(Path.Combine(source, "imports", "committed.bin"), content);
        var output = Path.Combine(fixture.Path, "uploads.tar.gz");
        var result = new DirectoryCapture().Capture(source, output, "uploads", CancellationToken.None);
        Assert.Equal("uploads", result.Name);
        using var gzip = new GZipStream(File.OpenRead(output), CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var names = new List<string>();
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            names.Add(entry.Name.TrimEnd('/'));
            if (entry.EntryType != TarEntryType.RegularFile) continue;
            using var bytes = new MemoryStream();
            entry.DataStream!.CopyTo(bytes);
            Assert.Equal(content, bytes.ToArray());
            Assert.Equal(0, entry.Uid);
        }
        Assert.Equal(new[] { ".", "empty", "imports", "imports/committed.bin" }, names);
        Assert.Equal(content, File.ReadAllBytes(Path.Combine(source, "imports", "committed.bin")));
    }

    [Fact]
    public void CaptureRejectsSymlinkWithoutReadingItsTarget()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TestDirectory();
        var source = Path.Combine(fixture.Path, "uploads");
        Directory.CreateDirectory(source);
        var link = Path.Combine(source, "unsafe");
        File.CreateSymbolicLink(link, "/etc/passwd");
        try
        {
            Assert.Throws<IOException>(() => new DirectoryCapture().Capture(source,
                Path.Combine(fixture.Path, "uploads.tar.gz"), "uploads", CancellationToken.None));
        }
        finally { File.Delete(link); }
    }

    [Fact]
    public void PublicationCannotOverwriteExistingArchive()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TestDirectory();
        using var directory = new SafeDirectory(fixture.Path);
        using (var first = directory.Write("archive")) first.WriteByte(42);
        using (var stagedFile = directory.Write("partial")) { stagedFile.WriteByte(99); }
        Assert.Throws<IOException>(() => directory.Publish("partial", "archive"));
        Assert.Equal(new byte[] { 42 }, File.ReadAllBytes(Path.Combine(fixture.Path, "archive")));
        Assert.True(File.Exists(Path.Combine(fixture.Path, "partial")));
    }

    [Fact]
    public void EmptyExistingUploadsAreValidButMissingRootFails()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new TestDirectory();
        var source = Path.Combine(fixture.Path, "uploads");
        Assert.Throws<IOException>(() => new DirectoryCapture().Capture(source,
            Path.Combine(fixture.Path, "missing.tar.gz"), "uploads", CancellationToken.None));
        Directory.CreateDirectory(source);
        var result = new DirectoryCapture().Capture(source, Path.Combine(fixture.Path, "uploads.tar.gz"), "uploads", CancellationToken.None);
        Assert.True(result.Length > 0);
        Assert.Empty(Directory.EnumerateFileSystemEntries(source));
    }
    [Fact]
    public void ManifestCannotOmitRequiredInterpretationFields()
    {
        Assert.Throws<System.Text.Json.JsonException>(() =>
            System.Text.Json.JsonSerializer.Deserialize<RecoveryManifest>("{}", ArchiveContract.Json));
    }

    [Fact]
    public async Task VerificationRejectsCorruptionWithoutChangingInputOrExecutingDatabaseTools()
    {
        using var fixture = new TestDirectory();
        var path = Path.Combine(fixture.Path, "owned.tar");
        await File.WriteAllBytesAsync(path, [1, 2, 3]);
        await File.WriteAllTextAsync(path + ".sha256", new string('0', 64) + "  owned.tar\n");
        using var directory = new SafeDirectory(fixture.Path);
        await Assert.ThrowsAsync<IOException>(() => ArchiveVerifier.VerifyAsync(directory, "owned.tar",
            Path.Combine(fixture.Path, "unused"), new SourceIdentity(), Guid.NewGuid(), CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(path));
        Assert.False(Directory.Exists(Path.Combine(fixture.Path, "unused")));
    }

    /// <summary>Listing refuses excessive scan work even when none of the names is an owned archive.</summary>
    [Fact]
    public void DirectoryScanHasAnInputBound()
    {
        using var fixture = new TestDirectory();
        for (var i = 0; i < 5; i++) File.WriteAllText(Path.Combine(fixture.Path, i.ToString()), "foreign");
        using var directory = new SafeDirectory(fixture.Path);
        Assert.Throws<IOException>(() => directory.Names(4));
        Assert.Equal(5, directory.Names(5).Length);
    }

    /// <summary>Daily UTC jitter is stable; long downtime selects one slot and clock rollback cannot replay success.</summary>
    [Fact]
    public void SchedulerCatchupRetryAndRollbackRemainBounded()
    {
        var config = new WorkerConfiguration { Installation = Guid.Parse("53300000-0000-0000-0000-000000000001") };
        var now = new DateTimeOffset(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        var slot = RecoveryScheduler.DueSlot(config, now);
        Assert.InRange(slot.Hour * 60 + slot.Minute, 180, 195);
        Assert.Equal(slot, RecoveryScheduler.DueSlot(config, now.AddHours(1)));
        var state = new SchedulerReceipt(1, slot, 1, true, now, null, Guid.NewGuid(), now, "none");
        state.Validate();
        Assert.Throws<IOException>(() => (state with { Schema = 2 }).Validate());
        Assert.Throws<IOException>(() => (state with { Attempts = 4 }).Validate());
        Assert.Throws<IOException>(() => (state with { NextRetry = now }).Validate());
        Assert.False(RecoveryScheduler.ShouldAttempt(state, slot, now, 3));
        Assert.False(RecoveryScheduler.ShouldAttempt(state, slot.AddDays(-1), now, 3));
        Assert.True(RecoveryScheduler.ShouldAttempt(state, slot.AddDays(20), now.AddDays(20), 3));
        Assert.False(RecoveryScheduler.ShouldAttempt(state with { Succeeded = false, Attempts = 3 }, slot, now, 3));
        Assert.False(RecoveryScheduler.ShouldAttempt(state with { Succeeded = false, NextRetry = now.AddMinutes(1) }, slot, now, 3));
        Assert.True(RecoveryScheduler.ShouldAttempt(state with { Succeeded = false, NextRetry = now.AddMinutes(-1) }, slot, now, 3));
    }

    /// <summary>Manifest/source strings are bounded inert data, never terminal or command fragments.</summary>
    [Fact]
    public void SourceIdentityRejectsControlCharactersAndPartialSchema()
    {
        var source = new SourceIdentity
        {
            ApplicationVersion = "1.9.19", SourceRevision = new string('a', 40),
            ApplicationImage = "ghcr.io/stef-k/wayfarer@sha256:" + new string('b', 64),
            DatabaseImage = "ghcr.io/stef-k/wayfarer-db@sha256:" + new string('c', 64),
            BundleFingerprint = new string('d', 64), PayloadFingerprint = new string('e', 64),
            Project = "fixture", WorkerVersion = "1", QuartzIdentity = "wayfarer-quartz-schema-v1",
            ExpectedMigrations = ["20260924220353_StablePersonalCredentialCompanion"]
        };
        ArchiveContract.ValidateSource(source);
        ArchiveContract.ValidateSource(source with { Kind = "native", ApplicationImage = "", DatabaseImage = "", Project = "", BundleFingerprint = "" });
        Assert.Throws<IOException>(() => ArchiveContract.ValidateSource(source with { ApplicationVersion = "1.0\nunsafe" }));
        Assert.Throws<IOException>(() => ArchiveContract.ValidateSource(source with { ExpectedMigrations = [] }));
        Assert.Throws<IOException>(() => ArchiveContract.ValidateSource(source with { SourceRevision = "unknown" }));
    }

    /// <summary>Even a matching final checksum cannot authorize unexpected members, links or path escape.</summary>
    [Theory]
    [InlineData("../escape", false)]
    [InlineData("unexpected", false)]
    [InlineData("uploads.tar.gz", true)]
    public async Task UnsafeOuterMembersFailBeforeToolsOrExtraction(string member, bool link)
    {
        using var fixture = new TestDirectory();
        var archive = Path.Combine(fixture.Path, "unsafe.tar");
        using (var output = File.Create(archive))
        using (var writer = new TarWriter(output, TarEntryFormat.Ustar))
        {
            var entry = new UstarTarEntry(link ? TarEntryType.SymbolicLink : TarEntryType.RegularFile, member);
            if (link) entry.LinkName = "/outside";
            else entry.DataStream = new MemoryStream([1, 2, 3]);
            writer.WriteEntry(entry);
        }
        var digest = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(archive)));
        await File.WriteAllTextAsync(archive + ".sha256", digest + "  unsafe.tar\n");
        using var directory = new SafeDirectory(fixture.Path);
        var staging = Path.Combine(fixture.Path, "staging");
        Directory.CreateDirectory(staging);
        await Assert.ThrowsAsync<IOException>(() => ArchiveVerifier.VerifyAsync(directory, "unsafe.tar", staging,
            new SourceIdentity(), Guid.NewGuid(), CancellationToken.None));
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
    }

    /// <summary>Duplicate interpretation fields are rejected identically by listing and full verification.</summary>
    [Fact]
    public void DuplicateManifestPropertiesAreRejected()
    {
        using var input = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"Format\":\"wayfarer-recovery\",\"Format\":\"other\"}"));
        Assert.Throws<IOException>(() => ArchiveContract.ReadManifest(input));
    }

}
