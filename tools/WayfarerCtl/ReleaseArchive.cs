using System.Formats.Tar;
using System.IO.Compression;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Bounded USTAR/gzip staging; the runtime parses tar checksums/paths and the existing validator grants authority.</summary>
public static class ReleaseArchive
{
    public const long FileLimit = 256L * 1024 * 1024;
    public const long TotalLimit = 768L * 1024 * 1024;

    /// <summary>Only fixed regular files can be created, exclusively, below an owned empty staging directory.</summary>
    public static async Task<ReleaseBundle> ExtractAsync(string archive, string stage, CancellationToken token)
    {
        var expanded = Path.Combine(stage, "expanded.tar");
        using (var source = File.OpenRead(archive))
        using (var gzip = new GZipStream(source, CompressionMode.Decompress))
        using (var target = new FileStream(expanded, new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = ProtectedFiles.PrivateFile
        }))
            await PublicReleaseAcquisition.CopyAsync(gzip, target, TotalLimit, token);
        var destination = Path.Combine(stage, "bundle");
        if (Path.Exists(destination)) throw new IOException("Extraction destination already exists.");
        Directory.CreateDirectory(destination, ProtectedFiles.PrivateDirectory);
        var allowed = ReleaseContract.Payloads.Concat(ReleaseContract.CapturePayloads).Append("release.json").ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var tar = File.OpenRead(expanded);
        long offset = 0;
        var header = new byte[512];
        while (offset < tar.Length)
        {
            token.ThrowIfCancellationRequested();
            tar.Position = offset;
            await tar.ReadExactlyAsync(header, token);
            if (header.All(value => value == 0))
            {
                // Accept only the bounded zero trailer emitted by the deterministic assembler; hidden concatenated entries fail.
                if (tar.Length - offset is < 1024 or > 10240) throw new IOException("Invalid archive trailer.");
                while (tar.Position < tar.Length)
                    if (tar.ReadByte() != 0) throw new IOException("Nonzero archive trailer.");
                break;
            }
            // Reject extension/sparse/link headers BEFORE TarReader can allocate extended metadata from attacker-controlled size.
            if (header[156] != (byte)'0' || !header.AsSpan(257, 8).SequenceEqual("ustar\000"u8) ||
                header.AsSpan(157, 100).ContainsAnyExcept((byte)0)) throw new IOException("Only ordinary USTAR files are supported.");
            tar.Position = offset;
            using var reader = new TarReader(tar, leaveOpen: true);
            var entry = await reader.GetNextEntryAsync(copyData: false, cancellationToken: token)
                ?? throw new IOException("Missing archive entry.");
            if (entry.Format != TarEntryFormat.Ustar || entry.EntryType != TarEntryType.RegularFile ||
                !allowed.Contains(entry.Name) || !seen.Add(entry.Name) || seen.Count > allowed.Count ||
                entry.Length < 0 || entry.Length > (entry.Name == "release.json" ? ArchiveContract.ManifestLimit : FileLimit))
                throw new IOException("Invalid release archive path/type/duplicate/size.");
            var path = Path.Combine(destination, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!, ProtectedFiles.PrivateDirectory);
            using (var output = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = ProtectedFiles.PrivateFile
            }))
            {
                if (entry.DataStream is not null)
                    await PublicReleaseAcquisition.CopyAsync(entry.DataStream, output, entry.Length, token);
                if (output.Length != entry.Length) throw new IOException("Truncated archive payload.");
            }
            File.SetUnixFileMode(path, (UnixFileMode)ReleaseContract.Mode(entry.Name));
            offset = checked(offset + 512 + ((entry.Length + 511) / 512) * 512);
            if (offset >= tar.Length) throw new IOException("Missing archive trailer.");
        }
        return ReleaseBundle.Validate(destination);
    }
}
