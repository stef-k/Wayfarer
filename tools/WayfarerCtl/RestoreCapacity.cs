using System.Globalization;
using System.Text.Json;
using WayfarerRecovery;

namespace WayfarerCtl;

/// <summary>Reject obvious shortages without claiming compressed SQL predicts final database/index/WAL size.</summary>
public sealed class RestoreCapacity(IProcessRunner runner)
{
    public const long Reserve = 1073741824;

    /// <summary>Already retained archives and old/failed volumes consume measured free space and are never reclaimed here.</summary>
    public static void Require(string path, long additional)
    {
        using var directory = new SafeDirectory(path);
        Check(directory.AvailableBytes, additional);
    }

    /// <summary>Checked arithmetic fails closed; each filesystem retains a one-GiB operating reserve.</summary>
    public static void Check(long available, long additional)
    {
        if (additional < 0 || available < checked(additional + Reserve))
            throw new IOException("Insufficient restore capacity including reserve; old volumes and backups retained.");
    }

    /// <summary>Budget files, cache bootstrap and database/index/WAL growth using observed old DB size when available.</summary>
    public static long CandidateBytes(long dump, long files, long oldDatabase) =>
        checked(Math.Max(Reserve, Math.Max(checked(dump * 4), checked(oldDatabase * 2))) + files + Reserve);

    /// <summary>Reap the exact read-only probe before reuse; a daemon-loss retry cannot leave an unreceipted volume consumer.</summary>
    private static async Task ReclaimAsync(RestoreContainers owner, string name, string project, CancellationToken token)
    {
        var existing = (await owner.Required(["ps", "-a", "--format", "{{.Names}}"], token)).Split('\n');
        if (!existing.Contains(name)) return;
        using var document = JsonDocument.Parse(await owner.Required(["inspect", name], token));
        var container = document.RootElement[0];
        if (container.GetProperty("Config").GetProperty("Labels").GetProperty("wayfarer.restore-helper").GetString() != project ||
            container.GetProperty("Mounts").EnumerateArray().Any(mount => mount.GetProperty("RW").GetBoolean()))
            throw new IOException("Capacity helper ownership changed.");
        if (container.GetProperty("State").GetProperty("Status").GetString() != "created")
        {
            await owner.Required(["stop", "--time", "20", name], token);
            await owner.Required(["wait", name], token);
        }
        await owner.Required(["rm", name], token);
    }

    /// <summary>Measure local Docker storage read-only, then check all remaining allocations before fencing/staging/activation.</summary>
    public async Task CheckAsync(string root, RestoreReceipt receipt, CancellationToken token)
    {
        var plan = receipt.Plan;
        var directory = RestorePreparation.DirectoryFor(root, plan.Operation);
        var facts = JsonSerializer.Deserialize<VerifiedRestoreArchive>(File.ReadAllText(Path.Combine(directory, "capacity.json")))
            ?? throw new IOException("Missing verified capacity evidence.");
        if (facts.DatabaseDumpBytes <= 0 || facts.DatabaseDumpBytes > ArchiveContract.ByteLimit ||
            facts.ExpandedFileBytes < 0 || facts.ExpandedFileBytes > checked(ArchiveContract.ByteLimit * 2))
            throw new IOException("Invalid verified capacity evidence.");
        var owner = new RestoreContainers(runner);
        var dockerRoot = (await owner.Required(["info", "--format", "{{.DockerRootDir}}"], token)).Trim();
        if (!Path.IsPathFullyQualified(dockerRoot) || dockerRoot.Contains(',') || dockerRoot.Contains(':'))
            throw new IOException("Invalid local Docker storage path.");
        var mounts = new List<string> { "--mount", "type=bind,source=" + dockerRoot + ",target=/storage,readonly" };
        var script = "available=$(df -Pk /storage | tail -1 | awk '{print $4}'); db=0; files=0; ";
        if (!plan.NewInstall)
        {
            foreach (var (role, target) in new[] { ("db-data", "/old-db"), ("app-data", "/old-files") })
            {
                var volume = ActiveStorage.Volume(plan.Target, role);
                await owner.Required(["volume", "inspect", volume], token);
                mounts.AddRange(["--mount", "type=volume,source=" + volume + ",target=" + target + ",readonly"]);
            }
            script += "db=$(du -sk /old-db); db=${db%%[!0-9]*}; files=$(du -sk /old-files); files=${files%%[!0-9]*}; ";
        }
        script += "printf '%s %s %s\\n' \"$available\" \"$db\" \"$files\"";
        var name = plan.Target.Project + "-restore-capacity-" + plan.Operation.ToString("N");
        await ReclaimAsync(owner, name, plan.Target.Project, token);
        string output;
        try
        {
            output = await owner.RunAsync(name,
            ["--network=none", "--read-only", "--user=0", "--cap-drop=ALL", "--cap-add=DAC_READ_SEARCH",
                // Override the DB image's declared volume so this probe cannot create a writable anonymous DB volume.
                "--tmpfs=/var/lib/postgresql/data:ro,mode=000,size=65536",
                "--security-opt=no-new-privileges:true", .. mounts, "--entrypoint=sh",
                "ghcr.io/stef-k/wayfarer-db@" + plan.Target.DbDigest, "-ec", script], token);
        }
        catch
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
            try { await ReclaimAsync(owner, name, plan.Target.Project, cleanup.Token); }
            catch (Exception error) when (error is IOException or OperationCanceledException)
            { Console.Error.WriteLine("Read-only capacity helper remains for reconciliation."); }
            throw;
        }
        await ReclaimAsync(owner, name, plan.Target.Project, token);
        var sizes = output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => checked(long.Parse(value, CultureInfo.InvariantCulture) * 1024)).ToArray();
        if (sizes.Length != 3 || sizes.Any(value => value < 0)) throw new IOException("Invalid Docker capacity evidence.");
        var emergency = !plan.NewInstall && !plan.WithoutEmergencyBackup && receipt.EmergencyArchive is null &&
            receipt.Phase <= RestorePhase.Fenced ? checked(sizes[1] + sizes[2] + Reserve) : 0;
        // Conservatively include emergency spool/output even when destinations are separate filesystems.
        var candidate = receipt.Phase <= RestorePhase.Staging
            ? CandidateBytes(facts.DatabaseDumpBytes, facts.ExpandedFileBytes, sizes[1]) : 0;
        Check(sizes[0], checked(candidate + emergency * 3));
        Require(root, checked(candidate + emergency * 3));
        if (emergency != 0) Require(plan.Target.Backup!.Destination, emergency);
    }
}
