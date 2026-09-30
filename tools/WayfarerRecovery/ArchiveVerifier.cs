using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace WayfarerRecovery;

/// <summary>Non-destructive integrity verification; no database SQL or manifest-selected commands are executed.</summary>
public static class ArchiveVerifier
{
    /// <summary>Verify a complete pair and its components into an exclusively owned private task directory.</summary>
    public static async Task<VerifyResult> VerifyAsync(SafeDirectory destination, string name, string staging,
        SourceIdentity expected, Guid installation, CancellationToken token)
    {
        SafeDirectory.ValidateName(name);
        if (name.Contains('/')) throw new IOException("Archive selection must be a basename.");
        using var archive = destination.Read(name);
        if (archive.Length > ArchiveContract.ByteLimit) throw new IOException("Archive byte limit exceeded.");
        using var sidecar = destination.Read(name + ".sha256");
        var checksum = ReadText(sidecar, 512);
        var digest = Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, token));
        if (checksum != $"{digest}  {name}\n") throw new IOException("Final checksum mismatch.");
        archive.Position = 0;
        var manifest = await ReadOuterAsync(archive, staging, token);
        ArchiveContract.Validate(manifest);
        if (manifest.Installation != installation || name != ArchiveContract.Name(installation, manifest.Completed, manifest.Archive))
            throw new IOException("Archive ownership/name mismatch.");
        await VerifyComponentsAsync(manifest, staging, token);
        var uploadsBytes = ValidateDirectory(Path.Combine(staging, "uploads.tar.gz"), null, token);
        var keys = Path.Combine(staging, "keys");
        Directory.CreateDirectory(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var ringBytes = ValidateDirectory(Path.Combine(staging, "data-protection.tar.gz"), keys, token);
        // The private extracted copy is used only to prove usable key material; automatic generation stays disabled.
        if (!Directory.EnumerateFiles(keys, "key-*.xml").Any()) throw new IOException("Key ring is empty.");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keys), builder =>
            builder.SetApplicationName("Wayfarer").DisableAutomaticKeyGeneration());
        var protector = provider.CreateProtector("Wayfarer.Recovery.Verify.v1");
        if (protector.Unprotect(protector.Protect("ready")) != "ready") throw new IOException("Key ring is unusable.");
        await DatabaseCapture.RunAsync("pg_restore", ["--list", Path.Combine(staging, "database.dump")], null, token);
        return new VerifyResult(1, true, IsCompatible(manifest, expected), manifest.Archive, name, manifest.Mode)
        { ExpandedFileBytes = checked(uploadsBytes + ringBytes) };
    }

    /// <summary>Classify validated metadata only; callers must still verify bytes and require trusted SQL provenance.</summary>
    public static bool IsCompatible(RecoveryManifest manifest, SourceIdentity expected)
    {
        ArchiveContract.Validate(manifest);
        ArchiveContract.ValidateSource(expected);
        return manifest.Source.Kind == expected.Kind && manifest.Source.ApplicationImage == expected.ApplicationImage &&
            manifest.Source.DatabaseImage == expected.DatabaseImage && manifest.Source.SourceRevision == expected.SourceRevision &&
            manifest.Source.ApplicationName == expected.ApplicationName && QuartzCompatible(manifest.Source, expected) &&
            manifest.Source.ApplicationVersion == expected.ApplicationVersion && manifest.Source.Platform == expected.Platform &&
            manifest.Source.StableIdentity == "ready" && manifest.Database.Major == 18 &&
            manifest.Database.ServerVersion == "18.6 (Debian 18.6-1.pgdg12+2)" && manifest.Database.Name == "wayfarer" &&
            manifest.Database.Encoding == "UTF8" && manifest.Database.PostgisExtension == "3.6.4" && manifest.Database.Citext == "1.8" &&
            manifest.Database.Collation == "C.UTF-8" && manifest.Database.CharacterType == "C.UTF-8" && manifest.Database.LocaleProvider == "c" &&
            manifest.Database.PostgisExtension == manifest.Database.PostgisLibrary &&
            manifest.Database.Migrations.SequenceEqual(expected.ExpectedMigrations);
    }

    /// <summary>Only target-owned support can bridge legacy evidence; new snapshots are not target authority.</summary>
    private static bool QuartzCompatible(SourceIdentity source, SourceIdentity target) => target.ConfigurationSchema switch
    {
        2 => source.ConfigurationSchema == 2 && source.QuartzIdentity == target.QuartzIdentity,
        3 => source.ConfigurationSchema == 3
            ? source.QuartzCompatibilityContract == target.QuartzCompatibilityContract
            : target.SupportedLegacySourceSchemas!.Contains(2) &&
                source.BundleFingerprint == target.BundleFingerprint && source.PayloadFingerprint == target.PayloadFingerprint &&
                source.WorkerVersion == target.WorkerVersion && source.ReleaseStatus == target.ReleaseStatus,
        _ => false
    };

    private static async Task<RecoveryManifest> ReadOuterAsync(Stream archive, string staging, CancellationToken token)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        using var reader = new TarReader(archive, leaveOpen: true);
        TarEntry? entry;
        long total = 0;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            token.ThrowIfCancellationRequested();
            if (entry.Format != TarEntryFormat.Ustar || entry.EntryType != TarEntryType.RegularFile ||
                !ArchiveContract.Members.Contains(entry.Name) || !names.Add(entry.Name) || entry.DataStream is null ||
                entry.Length < 0 || entry.Length > ArchiveContract.ByteLimit) throw new IOException("Invalid outer archive member.");
            total = checked(total + entry.Length);
            if (total > ArchiveContract.ByteLimit || entry.Name == "manifest.json" && entry.Length > ArchiveContract.ManifestLimit ||
                entry.Name == "SHA256SUMS" && entry.Length > 1024) throw new IOException("Archive limit exceeded.");
            await using var output = new FileStream(Path.Combine(staging, entry.Name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await CopyBoundedAsync(entry.DataStream, output, entry.Length, token);
        }
        var padding = new byte[4096];
        int trailing;
        while ((trailing = await archive.ReadAsync(padding, token)) != 0)
            if (padding.AsSpan(0, trailing).ContainsAnyExcept((byte)0)) throw new IOException("Unexpected outer trailing data.");
        if (!names.SetEquals(ArchiveContract.Members)) throw new IOException("Recovery component missing.");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(staging, "manifest.json"), token);
        using var manifestStream = new MemoryStream(bytes);
        return ArchiveContract.ReadManifest(manifestStream);
    }

    private static async Task VerifyComponentsAsync(RecoveryManifest manifest, string staging, CancellationToken token)
    {
        var checksums = new StringBuilder();
        foreach (var name in ArchiveContract.Members[..4])
        {
            await using var stream = File.OpenRead(Path.Combine(staging, name));
            var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token));
            checksums.Append(hash).Append("  ").Append(name).Append('\n');
            if (name != "manifest.json")
            {
                var component = manifest.Components.Single(value => value.Member == name);
                if (component.Sha256 != hash || component.Length != stream.Length) throw new IOException("Component checksum/length mismatch.");
            }
        }
        if (await File.ReadAllTextAsync(Path.Combine(staging, "SHA256SUMS"), token) != checksums.ToString())
            throw new IOException("Internal checksums disagree.");
    }

    /// <summary>Validate bounded USTAR regular files/directories; optional extraction is only to an empty task-owned unprivileged target.</summary>
    public static long ValidateDirectory(string path, string? extract, CancellationToken token)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var tar = new TarReader(gzip);
        var names = new HashSet<string>(StringComparer.Ordinal);
        long total = 0;
        TarEntry? entry;
        while ((entry = tar.GetNextEntry()) is not null)
        {
            token.ThrowIfCancellationRequested();
            var name = entry.EntryType == TarEntryType.Directory ? entry.Name.TrimEnd('/') : entry.Name;
            if (name != "." || entry.EntryType != TarEntryType.Directory) SafeDirectory.ValidateName(name);
            if (entry.Format != TarEntryFormat.Ustar || entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.Directory) ||
                !names.Add(name) || names.Count > ArchiveContract.EntryLimit || entry.Length < 0)
                throw new IOException("Unsafe nested archive.");
            total = checked(total + entry.Length);
            if (total > ArchiveContract.ByteLimit) throw new IOException("Nested archive byte limit exceeded.");
            if (entry.EntryType == TarEntryType.Directory)
            {
                if (entry.Length != 0) throw new IOException("Directory carries data.");
                if (extract is not null) Directory.CreateDirectory(Path.Combine(extract, name), UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                continue;
            }
            if (entry.DataStream is null && entry.Length != 0) throw new IOException("File data missing.");
            if (extract is not null)
            {
                using var target = new SafeDirectory(extract);
                if (target.AvailableBytes < checked(entry.Length + 1073741824L)) throw new IOException("Insufficient extraction capacity including reserve.");
            }
            using var output = extract is null ? Stream.Null : ExtractFile(extract, name);
            if (entry.DataStream is not null) CopyBoundedAsync(entry.DataStream, output, entry.Length, token, verifyOutput: extract is not null).GetAwaiter().GetResult();
            if (output is FileStream fileOutput) fileOutput.Flush(flushToDisk: true);
        }
        if (!names.Contains(".")) throw new IOException("Nested root representation missing.");
        // Force gzip to its checksum/trailer rather than accepting a truncated compressed member.
        var tail = new byte[4096];
        int count;
        while ((count = gzip.Read(tail)) != 0)
        {
            token.ThrowIfCancellationRequested();
            total = checked(total + count);
            if (total > ArchiveContract.ByteLimit || tail.AsSpan(0, count).ContainsAnyExcept((byte)0))
                throw new IOException("Unexpected nested trailing data.");
        }
        if (extract is not null)
        {
            // Flush children before parents so activation never commits merely cached directory entries.
            foreach (var directory in Directory.EnumerateDirectories(extract, "*", SearchOption.AllDirectories)
                .OrderByDescending(value => value.Length).Append(extract))
            {
                token.ThrowIfCancellationRequested();
                using var owned = new SafeDirectory(directory);
                owned.Flush();
            }
        }
        return total;
    }

    private static Stream ExtractFile(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return new FileStream(path, new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.ReadWrite, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
    }

    private static string ReadText(Stream stream, int limit)
    {
        if (stream.Length > limit) throw new IOException("Metadata size limit exceeded.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long expected, CancellationToken token, bool verifyOutput = false)
    {
        using var digest = verifyOutput ? IncrementalHash.CreateHash(HashAlgorithmName.SHA256) : null;
        var buffer = new byte[65536];
        long copied = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            copied = checked(copied + count);
            if (copied > expected) throw new IOException("Archive member size mismatch.");
            digest?.AppendData(buffer.AsSpan(0, count));
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (copied != expected) throw new IOException("Truncated archive member.");
        if (digest is not null && output is FileStream file)
        {
            file.Flush(flushToDisk: true);
            file.Position = 0;
            var expectedHash = digest.GetHashAndReset();
            var actualHash = await SHA256.HashDataAsync(file, token);
            if (!expectedHash.SequenceEqual(actualHash))
                throw new IOException("Extracted file readback mismatch.");
        }
    }
}

/// <summary>Integrity and configured-source compatibility are separate observations, never a restore authorization.</summary>
public sealed record VerifyResult(int Schema, bool IntegrityValid, bool CompatibilitySupported, Guid Archive, string Name, string Mode)
{
    /// <summary>Verified uncompressed file bytes, including bounded padding, for restore capacity accounting.</summary>
    public long ExpandedFileBytes { get; init; }
}
