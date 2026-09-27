using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WayfarerRecovery;

/// <summary>One recovery-set owner for manual/scheduled capture, verification, publication and retention.</summary>
public sealed class RecoveryEngine(WorkerConfiguration config)
{
    /// <summary>Produce one online set while holding the installation-local recovery exclusion.</summary>
    public async Task<BackupResult> BackupAsync(DateTimeOffset? slot, CancellationToken token, string? hostOperation = null)
    {
        if (!config.Enabled) throw new IOException("Backup disabled.");
        using var exclusion = new RecoveryLock("/control/recovery.lock");
        return await BackupLockedAsync(slot, token, hostOperation);
    }

    /// <summary>Shared capture invoked only while the engine or scheduler owns recovery exclusion.</summary>
    internal async Task<BackupResult> BackupLockedAsync(DateTimeOffset? slot, CancellationToken token, string? hostOperation = null)
    {
        var quiesced = HostRecoveryOperation.Validate(hostOperation);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.DeadlineSeconds));
        using var destination = config.OpenDestination();
        using var staging = new RecoveryTaskDirectory();
        var started = DateTimeOffset.UtcNow;
        var database = await DatabaseCapture.CaptureAsync(staging.Path, "/run/secrets/app-password", config.Source, deadline.Token);
        var uploads = new DirectoryCapture().Capture("/source/" + config.Uploads, System.IO.Path.Combine(staging.Path, "uploads.tar.gz"), "uploads", deadline.Token);
        var ring = new DirectoryCapture().Capture("/source/" + config.Ring, System.IO.Path.Combine(staging.Path, "data-protection.tar.gz"), "data-protection", deadline.Token);
        var manifest = new RecoveryManifest
        {
            Installation = config.Installation, Archive = Guid.NewGuid(), Started = started, Completed = DateTimeOffset.UtcNow,
            Mode = quiesced ? "quiesced" : "online", Source = config.Source, Database = database.Identity, Components = [database.Component, ring, uploads], ScheduledSlot = slot
        };
        ArchiveContract.Validate(manifest);
        var name = ArchiveContract.Name(config.Installation, manifest.Completed, manifest.Archive);
        await AssembleAsync(manifest, staging.Path, name, deadline.Token);
        using (var local = new SafeDirectory(staging.Path))
        using (var verification = new RecoveryTaskDirectory())
        {
            var result = await ArchiveVerifier.VerifyAsync(local, name, verification.Path, config.Source, config.Installation, deadline.Token);
            if (!result.CompatibilitySupported) throw new IOException("Captured source compatibility failed.");
        }
        await PublishAsync(destination, staging.Path, name, deadline.Token);
        var retained = true;
        try { await RetainAsync(destination, deadline.Token); }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException) { retained = false; }
        return new BackupResult(1, manifest.Archive, name, manifest.Completed, retained);
    }

    /// <summary>Listing validates owned complete metadata pairs without claiming full integrity verification.</summary>
    public RecoveryManifest[] List(SafeDirectory destination, CancellationToken token = default)
    {
        var results = new List<RecoveryManifest>();
        var prefix = $"wayfarer-recovery-v1_{config.Installation:D}_";
        foreach (var name in destination.Names(4096).Where(name => name.StartsWith(prefix, StringComparison.Ordinal) && name.EndsWith(".tar")))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var file = destination.Read(name);
                if (file.Length > ArchiveContract.ByteLimit) continue;
                using var sidecar = destination.Read(name + ".sha256");
                if (sidecar.Length > 512) continue;
                var checksum = new StreamReader(sidecar).ReadToEnd();
                if (checksum.Length != 64 + 2 + name.Length + 1 || !checksum[..64].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') ||
                    checksum[64..] != "  " + name + "\n") continue;
                using var tar = new TarReader(file);
                var entry = tar.GetNextEntry();
                if (entry?.Name != "manifest.json" || entry.Format != TarEntryFormat.Ustar ||
                    entry.EntryType != TarEntryType.RegularFile || entry.Length > ArchiveContract.ManifestLimit || entry.DataStream is null) continue;
                var manifest = ArchiveContract.ReadManifest(entry.DataStream);
                if (manifest is null || manifest.Installation != config.Installation ||
                    name != ArchiveContract.Name(config.Installation, manifest.Completed, manifest.Archive)) continue;
                ArchiveContract.Validate(manifest);
                var structureValid = true;
                foreach (var member in ArchiveContract.Members[1..])
                {
                    token.ThrowIfCancellationRequested();
                    var component = tar.GetNextEntry();
                    if (component is null || component.Name != member || component.Format != TarEntryFormat.Ustar ||
                        component.EntryType != TarEntryType.RegularFile || component.Length < 0 || component.Length > ArchiveContract.ByteLimit ||
                        member == "SHA256SUMS" && component.Length > 1024)
                    { structureValid = false; break; }
                }
                if (structureValid && tar.GetNextEntry() is null) results.Add(manifest);
            }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException) { /* Incomplete/unowned pairs are ignored. */ }
        }
        return results.OrderByDescending(value => value.Completed).ToArray();
    }

    /// <summary>Only verified complete sets from this installation become retention candidates.</summary>
    private async Task RetainAsync(SafeDirectory destination, CancellationToken token)
    {
        var valid = new List<string>();
        foreach (var manifest in List(destination, token))
        {
            using var staging = new RecoveryTaskDirectory();
            var name = ArchiveContract.Name(config.Installation, manifest.Completed, manifest.Archive);
            try
            {
                await ArchiveVerifier.VerifyAsync(destination, name, staging.Path, config.Source, config.Installation, token);
                valid.Add(name);
            }
            catch (Exception error) when (error is IOException or JsonException or System.Security.Cryptography.CryptographicException) { }
        }
        foreach (var name in valid.Skip(config.Retention))
        {
            using var current = config.OpenDestination();
            // Remove the commit marker first; interrupted deletion never leaves a false complete set.
            current.Delete(name + ".sha256");
            current.Delete(name);
        }
    }

    private static async Task AssembleAsync(RecoveryManifest manifest, string staging, string name, CancellationToken token)
    {
        await File.WriteAllTextAsync(System.IO.Path.Combine(staging, "manifest.json"), JsonSerializer.Serialize(manifest, ArchiveContract.Json), token);
        var sums = new StringBuilder();
        foreach (var member in ArchiveContract.Members[..4])
        {
            await using var stream = File.OpenRead(System.IO.Path.Combine(staging, member));
            sums.Append(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream, token))).Append("  ").Append(member).Append('\n');
        }
        await File.WriteAllTextAsync(System.IO.Path.Combine(staging, "SHA256SUMS"), sums.ToString(), token);
        var path = System.IO.Path.Combine(staging, name);
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var tar = new TarWriter(output, TarEntryFormat.Ustar, leaveOpen: true);
            foreach (var member in ArchiveContract.Members)
            {
                await using var input = File.OpenRead(System.IO.Path.Combine(staging, member));
                await tar.WriteEntryAsync(new UstarTarEntry(TarEntryType.RegularFile, member)
                { DataStream = input, Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite, ModificationTime = DateTimeOffset.UnixEpoch }, token);
            }
        }
        await using var archive = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(archive, token));
        await File.WriteAllTextAsync(path + ".sha256", $"{hash}  {name}\n", token);
    }

    private async Task PublishAsync(SafeDirectory destination, string staging, string name, CancellationToken token)
    {
        var owned = new List<string>();
        try
        {
            foreach (var member in new[] { name, name + ".sha256" })
            {
                var partial = member + ".partial-" + Guid.NewGuid().ToString("N");
                using (var output = destination.Write(partial))
                {
                    owned.Add(partial);
                    await using var input = File.OpenRead(System.IO.Path.Combine(staging, member));
                    await input.CopyToAsync(output, token);
                    output.Flush(flushToDisk: true);
                    output.Position = 0; input.Position = 0;
                    var expectedHash = await SHA256.HashDataAsync(input, token);
                    var writtenHash = await SHA256.HashDataAsync(output, token);
                    if (!expectedHash.SequenceEqual(writtenHash))
                        throw new IOException("Destination write verification failed.");
                }
                token.ThrowIfCancellationRequested();
                using var current = config.OpenDestination();
                current.Publish(partial, member);
                owned.Remove(partial);
            }
            using var finalIdentity = config.OpenDestination();
        }
        finally
        {
            foreach (var partial in owned)
                try { destination.Delete(partial); }
                catch (IOException) { Console.Error.WriteLine("Owned partial cleanup failed; prior failure remains authoritative."); }
        }
    }
}

/// <summary>Valid publication and retention outcome are deliberately independent.</summary>
public sealed record BackupResult(int Schema, Guid Archive, string Name, DateTimeOffset Completed, bool RetentionSucceeded);

/// <summary>Task-private staging contains the only extraction targets and never overlaps source/destination authorities.</summary>
public sealed class RecoveryTaskDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine("/tmp", "wayfarer-recovery-" + Guid.NewGuid().ToString("N"));
    public RecoveryTaskDirectory() => Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Console.Error.WriteLine("Private task cleanup failed; prior result remains authoritative."); }
    }
}
