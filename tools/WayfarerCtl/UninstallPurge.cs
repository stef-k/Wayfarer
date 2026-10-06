using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>After durable volume removal, the embedded receipt owns only these bounded product trees and files.</summary>
internal static class UninstallPurge
{
    /// <summary>Stable local authority created by setup, backup configuration, release adoption, update and restore.</summary>
    private static readonly string[] Files = ["installation.json", "deployment.env", "setup-progress.json", "setup-complete",
        "restore-complete", "backup-identity", "backup-previous.json"];

    /// <summary>Private installation trees have concrete consumer ownership exceptions checked below; releases are retained.</summary>
    private static readonly string[] Directories = ["secrets", "deployment-generations", "storage-generations", "recovery-generations",
        "recovery-control", "update-plans", "restore-plans", "uninstall-plans", "uninstall-history"];

    /// <summary>Reject unknown and unresolved top-level authority before purge authorization and on cleanup replay.</summary>
    internal static void RequireCleanable(string root, UninstallPlan plan)
    {
        if (plan.Current.Backup is { } backup && (backup.Destination == root || backup.Destination.StartsWith(root + "/", StringComparison.Ordinal)))
            throw new UsageException("Backup storage overlaps purge root authority; it must remain untouched.");
        using var directory = OpenRoot(root);
        var allowed = Files.Concat(Directories).Concat(new[] { "releases", "operation.lock", "uninstall.json" }).ToHashSet(StringComparer.Ordinal);
        if (plan.StartingState == UninstallStartingState.Preserved)
        {
            var temporary = UninstallReceipt.TransferName(plan);
            if (directory.Names().Contains(temporary))
            {
                var expected = JsonSerializer.SerializeToUtf8Bytes(new UninstallReceipt { Plan = plan, PlanHash = plan.Hash() });
                if (!UninstallPreparation.Read(root, temporary, 2097152).SequenceEqual(expected))
                    throw new UsageException("Conflicting preserved-to-purge temporary receipt.");
                allowed.Add(temporary);
            }
        }
        if (directory.Names().Except(allowed, StringComparer.Ordinal).Any())
            throw new UsageException("Unknown or unresolved root entries prevent purge; preserve them for reconciliation.");
        foreach (var name in new[] { "host-operation.json", "update-in-progress", "restore-in-progress" })
            if (UninstallHistory.Exists(Path.Combine(root, "recovery-control", name)))
                throw new UsageException("Unresolved recovery authority prevents purge root cleanup.");
        RequireReleaseCache(root, directory);
    }

    /// <summary>Verify the terminal root through no-follow same-mount handles, without consulting deleted installation state.</summary>
    internal static void RequireTerminalRoot(string root, bool receipt, bool tombstone)
    {
        using var directory = OpenRoot(root);
        string[] names = ["releases", "operation.lock", .. receipt ? new[] { "uninstall.json" } : [],
            .. tombstone ? new[] { UninstallPurgeTombstone.Name } : []];
        var expected = names.Order(StringComparer.Ordinal);
        if (!directory.Names().SequenceEqual(expected)) throw new UsageException("Purge root still contains contradictory residue.");
        RequireReleaseCache(root, directory);
        using var operation = directory.Read("operation.lock");
        RequireFacts("operation.lock", SafeDirectory.Inspect(operation.SafeFileHandle), false);
    }

    /// <summary>Retain validated immutable releases and receipted import stages only; failed setup stages may contain secrets and must refuse.</summary>
    private static void RequireReleaseCache(string root, SafeDirectory directory)
    {
        using var releases = directory.Child("releases");
        RequireFacts("releases", releases.Identity, true);
        var names = releases.Names(4096);
        foreach (var name in names)
        {
            var stage = name.EndsWith(".json", StringComparison.Ordinal) ? name[..^5] : name;
            if (System.Text.RegularExpressions.Regex.IsMatch(stage, "\\A\\.stage-[a-f0-9]{32}\\z"))
            {
                var bytes = UninstallPreparation.Read(root, Path.Combine("releases", stage + ".json"), 1024);
                var owner = JsonSerializer.Deserialize<ReleaseAuthority>(bytes, ArchiveContract.Json)
                    ?? throw new IOException("Missing retained release placement owner.");
                owner.Validate();
                if (names.Contains(owner.Name)) _ = ReleaseStore.Select(root, owner);
                if (name == stage)
                {
                    using var partial = releases.Child(stage);
                    RequireReleaseStage(partial, "");
                }
                continue;
            }
            using var published = releases.Child(name);
            var bundle = ReleaseBundle.Validate(Path.Combine(root, "releases", name), installed: true);
            if (bundle.Manifest.Name != name) throw new IOException("Retained release placement differs from its immutable owner.");
        }
    }

    /// <summary>Incomplete import payloads remain evidence, bounded to the fixed release inventory, private folders and safe regular files.</summary>
    private static void RequireReleaseStage(SafeDirectory stage, string prefix)
    {
        RequireFacts("releases", stage.Identity, true);
        foreach (var name in stage.Names(16))
        {
            var relative = prefix + name;
            if (prefix.Length == 0 && ReleaseContract.Directories.Append("capture").Contains(name))
            {
                using var child = stage.Child(name);
                RequireReleaseStage(child, name + "/");
                continue;
            }
            if (!ReleaseContract.Payloads.Concat(ReleaseContract.CapturePayloads).Append("release.json").Contains(relative))
                throw new IOException("Unrecognized retained release staging content.");
            using var file = stage.Read(name);
            var facts = SafeDirectory.Inspect(file.SafeFileHandle);
            var limit = relative == "release.json" ? ArchiveContract.ManifestLimit : 256L * 1024 * 1024;
            if (facts.User != 0 || facts.Group != 0 || facts.Size > (ulong)limit ||
                (facts.Mode & 0xfff) != 0x180 && (facts.Mode & 0xfff) != ReleaseContract.Mode(relative))
                throw new IOException("Unsafe retained release staging file.");
        }
    }

    /// <summary>Keep the complete receipt throughout partial cleanup, then publish minimal evidence before unlinking its predecessor.</summary>
    internal static void Complete(string root, UninstallReceipt receipt, Action<string>? checkpoint = null)
    {
        var tombstone = UninstallPurgeTombstone.From(root, receipt);
        tombstone.Validate(root);
        if (JsonSerializer.Serialize(UninstallReceipt.Load(root)) != JsonSerializer.Serialize(receipt))
            throw new UsageException("Current purge cleanup authority changed.");
        var existing = UninstallPurgeTombstone.Load(root);
        if (existing is not null)
        {
            existing.RequireMatch(root, receipt);
            using var terminal = OpenRoot(root);
            terminal.Flush();
            terminal.Delete("uninstall.json");
            RequireTerminalRoot(root, false, true);
            return;
        }
        RequireCleanable(root, receipt.Plan);
        using var directory = OpenRoot(root);
        var remaining = ArchiveContract.EntryLimit;
        foreach (var name in Directories)
        {
            if (!directory.Names().Contains(name)) continue;
            using var child = directory.Child(name);
            Clear(child, name, 0, ref remaining);
            directory.RemoveChild(name, child);
            checkpoint?.Invoke(name);
        }
        foreach (var name in Files)
        {
            if (!directory.Names().Contains(name)) continue;
            DeleteFile(directory, name, name);
            checkpoint?.Invoke(name);
        }
        RequireTerminalRoot(root, true, false);
        ProtectedFiles.Create(Path.Combine(root, UninstallPurgeTombstone.Name), JsonSerializer.Serialize(tombstone));
        directory.Flush();
        checkpoint?.Invoke(UninstallPurgeTombstone.Name);
        tombstone.RequireMatch(root, receipt);
        directory.Delete("uninstall.json");
        RequireTerminalRoot(root, false, true);
    }

    /// <summary>Recurse only through a verified product tree; links, hard links, mounts, unsafe identities and oversized traversal refuse.</summary>
    private static void Clear(SafeDirectory directory, string relative, int depth, ref int remaining)
    {
        if (depth > 33 || --remaining < 0) throw new IOException("Purge tree bound exceeded.");
        RequireFacts(relative, directory.Identity, true);
        foreach (var name in directory.Names())
        {
            var path = relative + "/" + name;
            SafeDirectory? child = null;
            try { child = directory.Child(name); }
            catch (IOException) { DeleteFile(directory, name, path); }
            if (child is null) { if (--remaining < 0) throw new IOException("Purge tree bound exceeded."); continue; }
            using (child)
            {
                Clear(child, path, depth + 1, ref remaining);
                directory.RemoveChild(name, child);
            }
        }
        directory.Flush();
    }

    /// <summary>Only an independently opened single-link regular file with the exact product mode/consumer identity can be unlinked.</summary>
    private static void DeleteFile(SafeDirectory directory, string name, string relative)
    {
        using var file = directory.Read(name);
        RequireFacts(relative, SafeDirectory.Inspect(file.SafeFileHandle), false);
        directory.Delete(name);
    }

    /// <summary>Private defaults permit the shipped root-owned generation parents and exact secrets/worker/frozen/recovery-lock exceptions.</summary>
    private static void RequireFacts(string relative, SafeDirectory.Facts facts, bool directory)
    {
        uint user = 0, group = 0;
        var mode = directory ? 0x1c0 : 0x180;
        var parts = relative.Split('/');
        // Directory.CreateDirectory(path, 0700) leaves intermediate generation/plan parents at the host's default 0755.
        if (directory && (relative is "deployment-generations" or "storage-generations" or "recovery-generations" or "update-plans" or "restore-plans") &&
            (facts.Mode & 0xfff) == 0x1ed) mode = 0x1ed;
        if (relative == "secrets/db-app-password") user = group = 999;
        if (relative == "secrets/app-password" || parts is ["recovery-generations", _, "worker.json"] ||
            parts is ["restore-plans", _, var secret] && secret.StartsWith("bootstrap-secret-", StringComparison.Ordinal) &&
            Guid.TryParseExact(secret[17..], "N", out _)) user = group = 1654;
        if (relative == "recovery-control") mode = 0x1ed;
        if (relative == "recovery-control/recovery.lock") { group = 1654; mode = 0x1b0; }
        if (relative == "recovery-control/state" || relative.StartsWith("recovery-control/state/", StringComparison.Ordinal) ||
            parts.Length >= 3 && parts[0] == "restore-plans" && (parts[2] == "verified" ||
                parts[2].StartsWith("verified-", StringComparison.Ordinal) && Guid.TryParseExact(parts[2][9..], "N", out _))) user = group = 1654;
        if (parts is ["restore-plans", _, "source.json"] || parts.Length >= 3 && parts[0] == "restore-plans" && parts[2] == "frozen")
        { group = 1654; mode = directory ? 0x1e8 : 0x1a0; }
        if ((directory ? !facts.IsDirectory : !facts.IsFile) || facts.User != user || facts.Group != group || (facts.Mode & 0xfff) != mode)
            throw new UsageException($"Unsafe purge tree ownership, mode or type at {relative}; state retained.");
    }

    /// <summary>All root entry selection and removal uses a protected local Linux directory handle.</summary>
    private static SafeDirectory OpenRoot(string root)
    {
        ProtectedFiles.SafePath(root);
        ProtectedFiles.Check(root, 0, directory: true);
        var directory = new SafeDirectory(root);
        try { directory.RequireLocalControl(); return directory; }
        catch { directory.Dispose(); throw; }
    }
}
