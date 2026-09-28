using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WayfarerRecovery;

/// <summary>One recovery-set owner for manual/scheduled capture, verification, publication and retention.</summary>
public sealed class RecoveryEngine(WorkerConfiguration config)
{
    /// <summary>Qualification-only crash gate after durable publication and before retention; never configured by worker input.</summary>
    internal Action? PublicationCommitted { get; init; }

    /// <summary>Qualification-only interruption at the durable hold/publication boundary.</summary>
    internal Action? HoldCreated { get; init; }

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
        CleanupStale(destination, DateTimeOffset.UtcNow, deadline.Token);
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
        if (HostRecoveryOperation.RequiresHold(hostOperation))
        {
            using var hold = destination.Write(name + ".restore-hold");
            hold.Write(System.Text.Encoding.UTF8.GetBytes(manifest.Archive.ToString("D") + "\n"));
            hold.Flush(true);
            destination.Flush();
        }
        try
        {
            HoldCreated?.Invoke();
            await PublishAsync(destination, staging.Path, name, deadline.Token);
        }
        catch
        {
            // Reconcile against the actual pair: publication may have committed before acknowledgement failed.
            try { CleanupStale(destination, DateTimeOffset.UtcNow, CancellationToken.None); }
            catch (IOException) { Console.Error.WriteLine("Publication residue remains for locked reconciliation."); }
            throw;
        }
        PublicationCommitted?.Invoke();
        var retained = await CompleteRetentionAsync(destination, deadline.Token);
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
        return results.OrderByDescending(value => value.Completed).ThenByDescending(value => ArchiveContract.Name(value.Installation, value.Completed, value.Archive), StringComparer.Ordinal).ToArray();
    }

    /// <summary>Publication stays valid when retention fails; both capture and reconciliation report the same outcome.</summary>
    internal async Task<bool> CompleteRetentionAsync(SafeDirectory destination, CancellationToken token)
    {
        try { await RetainAsync(destination, token); return true; }
        catch (Exception error) when (error is IOException or OperationCanceledException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Under recovery exclusion, reclaim day-old private residue in this installation's exact publication namespace.</summary>
    internal void CleanupStale(SafeDirectory destination, DateTimeOffset now, CancellationToken token)
    {
        var names = destination.Names(4096).ToHashSet(StringComparer.Ordinal);
        var pattern = "^wayfarer-recovery-v1_" + config.Installation.ToString("D") +
            @"_([0-9]{8}T[0-9]{13}Z)_([a-f0-9]{8}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{4}-[a-f0-9]{12})\.tar(\.sha256)?(\.partial-[a-f0-9]{32})?(\.restore-hold)?\z";
        foreach (var name in names)
        {
            token.ThrowIfCancellationRequested();
            var match = System.Text.RegularExpressions.Regex.Match(name, pattern);
            if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups[1].Value, "yyyyMMdd'T'HHmmssfffffff'Z'",
                    System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var completed) ||
                !Guid.TryParseExact(match.Groups[2].Value, "D", out var archive) || archive == Guid.Empty) continue;
            var canonical = ArchiveContract.Name(config.Installation, completed, archive);
            var hold = match.Groups[5].Success;
            if (hold && (match.Groups[3].Success || match.Groups[4].Success)) continue;
            var partial = match.Groups[4].Success;
            var sidecar = match.Groups[3].Success;
            if (hold ? names.Contains(canonical) && names.Contains(canonical + ".sha256") :
                !partial && names.Contains(sidecar ? canonical : canonical + ".sha256")) continue;
            SafeDirectory.Facts facts;
            try
            {
                using var file = destination.Read(name);
                facts = SafeDirectory.Inspect(file.SafeFileHandle);
                if (facts.User != 1654 || facts.Group != 1654 || (facts.Mode & 0x1ff) != 0x180 ||
                    !hold && facts.ModifiedSeconds > now.AddDays(-1).ToUnixTimeSeconds()) continue;
                // A final orphan must additionally carry its exact manifest/sidecar identity. Partials can be truncated anywhere.
                if (hold)
                {
                    if (file.Length != 37 || new StreamReader(file, leaveOpen: true).ReadToEnd() != archive.ToString("D") + "\n") continue;
                }
                else if (!partial && sidecar)
                {
                    if (file.Length > 512) continue;
                    var checksum = new StreamReader(file, leaveOpen: true).ReadToEnd();
                    if (checksum.Length != 64 + 2 + canonical.Length + 1 || checksum[64..] != "  " + canonical + "\n" ||
                        !checksum[..64].All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')) continue;
                }
                else if (!partial)
                {
                    using var tar = new TarReader(file, leaveOpen: true);
                    var entry = tar.GetNextEntry();
                    if (entry?.Name != "manifest.json" || entry.Format != TarEntryFormat.Ustar ||
                        entry.EntryType != TarEntryType.RegularFile || entry.Length > ArchiveContract.ManifestLimit || entry.DataStream is null) continue;
                    var manifest = ArchiveContract.ReadManifest(entry.DataStream);
                    ArchiveContract.Validate(manifest);
                    if (manifest.Installation != config.Installation || canonical != ArchiveContract.Name(manifest.Installation, manifest.Completed, manifest.Archive)) continue;
                }
            }
            catch (Exception error) when (error is IOException or JsonException or ArgumentException)
            { continue; } // Invalid, linked, truncated or foreign entries are never deletion authority.
            using var current = config.OpenDestination();
            using var candidate = current.Read(name);
            if (!facts.Equals(SafeDirectory.Inspect(candidate.SafeFileHandle))) throw new IOException("Residue changed during cleanup.");
            current.Delete(name);
        }
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
        var held = destination.Names(4096).Where(name => name.EndsWith(".restore-hold", StringComparison.Ordinal))
            .Select(name => name[..^13]).ToHashSet(StringComparer.Ordinal);
        foreach (var name in valid.Where(name => !held.Contains(name)).Skip(config.Retention))
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
