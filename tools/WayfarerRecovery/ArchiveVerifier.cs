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
        await DatabaseCapture.RunAsync("pg_restore", ["--list", Path.Combine(staging, "database.dump")], null, token);
        ValidateDirectory(Path.Combine(staging, "uploads.tar.gz"), null, token);
        var keys = Path.Combine(staging, "keys");
        Directory.CreateDirectory(keys, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ValidateDirectory(Path.Combine(staging, "data-protection.tar.gz"), keys, token);
        // The private extracted copy is used only to prove usable key material; automatic generation stays disabled.
        if (!Directory.EnumerateFiles(keys, "key-*.xml").Any()) throw new IOException("Key ring is empty.");
        var provider = DataProtectionProvider.Create(new DirectoryInfo(keys), builder =>
            builder.SetApplicationName("Wayfarer").DisableAutomaticKeyGeneration());
        var protector = provider.CreateProtector("Wayfarer.Recovery.Verify.v1");
        if (protector.Unprotect(protector.Protect("ready")) != "ready") throw new IOException("Key ring is unusable.");
        var compatible = manifest.Source.ApplicationImage == expected.ApplicationImage &&
            manifest.Source.DatabaseImage == expected.DatabaseImage && manifest.Source.SourceRevision == expected.SourceRevision &&
            manifest.Source.ApplicationName == expected.ApplicationName && manifest.Source.QuartzIdentity == expected.QuartzIdentity &&
            manifest.Database.Major == 17 && manifest.Database.Migrations.SequenceEqual(expected.ExpectedMigrations);
        return new VerifyResult(1, true, compatible, manifest.Archive, name, manifest.Mode);
    }

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
        if (!names.SetEquals(ArchiveContract.Members)) throw new IOException("Recovery component missing.");
        var bytes = await File.ReadAllBytesAsync(Path.Combine(staging, "manifest.json"), token);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicates(document.RootElement);
        return JsonSerializer.Deserialize<RecoveryManifest>(bytes, ArchiveContract.Json) ?? throw new IOException("Manifest missing.");
    }

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new IOException("Duplicate manifest property.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicates(item);
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

    /// <summary>Validate bounded USTAR regular files/directories; optional extraction is only to a task-owned ring copy.</summary>
    private static void ValidateDirectory(string path, string? extract, CancellationToken token)
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
                if (extract is not null) Directory.CreateDirectory(Path.Combine(extract, name));
                continue;
            }
            if (entry.DataStream is null && entry.Length != 0) throw new IOException("File data missing.");
            using var output = extract is null ? Stream.Null : ExtractFile(extract, name);
            if (entry.DataStream is not null) CopyBoundedAsync(entry.DataStream, output, entry.Length, token).GetAwaiter().GetResult();
        }
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
    }

    private static Stream ExtractFile(string root, string name)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return new FileStream(path, new FileStreamOptions
        { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite });
    }

    private static string ReadText(Stream stream, int limit)
    {
        if (stream.Length > limit) throw new IOException("Metadata size limit exceeded.");
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return reader.ReadToEnd();
    }

    private static async Task CopyBoundedAsync(Stream input, Stream output, long expected, CancellationToken token)
    {
        var buffer = new byte[65536];
        long copied = 0;
        int count;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            copied = checked(copied + count);
            if (copied > expected) throw new IOException("Archive member size mismatch.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
        }
        if (copied != expected) throw new IOException("Truncated archive member.");
    }
}

/// <summary>Integrity and configured-source compatibility are separate observations, never a restore authorization.</summary>
public sealed record VerifyResult(int Schema, bool IntegrityValid, bool CompatibilitySupported, Guid Archive, string Name, string Mode);
