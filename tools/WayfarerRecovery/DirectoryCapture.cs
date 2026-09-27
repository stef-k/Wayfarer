using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace WayfarerRecovery;

/// <summary>Read-only deterministic directory capture with handle-relative traversal and observed-change rejection.</summary>
public sealed class DirectoryCapture
{
    private int entries;
    private long bytes;
    private readonly Dictionary<string, SafeDirectory.Facts> inventory = new(StringComparer.Ordinal);

    /// <summary>Capture all regular content; missing roots, links, mounts and changed sources fail closed.</summary>
    public RecoveryComponent Capture(string source, string output, string logicalName, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        using var root = new SafeDirectory(source);
        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar))
        {
            // An explicit root preserves legitimate empty Uploads as a real archive.
            tar.WriteEntry(Entry(TarEntryType.Directory, "."));
            Walk(root, "", tar, token);
            foreach (var (name, expected) in inventory)
            {
                token.ThrowIfCancellationRequested();
                using var current = root.Read(name);
                if (!expected.Equals(SafeDirectory.Inspect(current.SafeFileHandle)))
                    throw new IOException("Source changed during capture.");
            }
        }
        using var result = File.OpenRead(output);
        return new RecoveryComponent(logicalName, Path.GetFileName(output), result.Length,
            Convert.ToHexStringLower(SHA256.HashData(result)), started, DateTimeOffset.UtcNow);
    }

    private void Walk(SafeDirectory directory, string prefix, TarWriter writer, CancellationToken token)
    {
        var before = directory.Identity;
        var names = directory.Names();
        foreach (var name in names)
        {
            token.ThrowIfCancellationRequested();
            if (++entries > ArchiveContract.EntryLimit) throw new IOException("Source entry limit exceeded.");
            var relative = prefix + name;
            SafeDirectory.ValidateName(relative);
            SafeDirectory? child = null;
            try { child = directory.Child(name); }
            catch (IOException) { /* A regular file is opened independently below; unsafe types still fail. */ }
            if (child is not null)
            {
                using (child)
                {
                    writer.WriteEntry(Entry(TarEntryType.Directory, relative));
                    Walk(child, relative + "/", writer, token);
                }
            }
            else
            {
                using var file = directory.Read(name);
                var identity = SafeDirectory.Inspect(file.SafeFileHandle);
                inventory.Add(relative, identity);
                bytes = checked(bytes + file.Length);
                if (bytes > ArchiveContract.ByteLimit) throw new IOException("Source byte limit exceeded.");
                var entry = Entry(TarEntryType.RegularFile, relative);
                entry.DataStream = file;
                writer.WriteEntry(entry);
                if (!identity.Equals(SafeDirectory.Inspect(file.SafeFileHandle))) throw new IOException("Source changed during capture.");
            }
        }
        if (!before.Equals(directory.Identity) || !names.SequenceEqual(directory.Names()))
            throw new IOException("Source inventory changed during capture.");
    }

    /// <summary>Archive ownership/modes are normalized, never instructions to reproduce source host identities.</summary>
    private static UstarTarEntry Entry(TarEntryType type, string name) => new(type, name)
    {
        Uid = 0, Gid = 0, UserName = "", GroupName = "", ModificationTime = DateTimeOffset.UnixEpoch,
        Mode = type == TarEntryType.Directory
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite
    };
}
