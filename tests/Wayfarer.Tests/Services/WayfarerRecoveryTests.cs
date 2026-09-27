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

}
