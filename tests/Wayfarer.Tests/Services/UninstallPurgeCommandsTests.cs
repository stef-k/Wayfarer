using System.Security.Cryptography;
using System.Text.Json;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Purge uses real protected authority and the existing process seam to prove exact resource erasure and exclusions.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class UninstallPurgeCommandsTests
{
    /// <summary>Both active choices share fencing and removal; exact verified publication remains sufficient when retention fails.</summary>
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ActivePurgeUsesSharedBackupAndRuntime(bool backup, bool retention)
    {
        using var fixture = new UninstallCommandFixture(true, true);
        fixture.Runner.RetentionSucceeded = retention;
        var marker = Path.Combine(fixture.Config.Backup!.Destination, ".wayfarer-recovery");
        var markerBytes = File.ReadAllBytes(marker);
        var oldArchive = Path.Combine(fixture.Config.Backup.Destination, "old-archive-sentinel.tar");
        ProtectedFiles.Create(oldArchive, "old recovery bytes", 1654);
        using var destination = new SafeDirectory(fixture.Config.Backup.Destination);
        var identity = destination.Identity;
        AddSentinels(fixture);
        var plan = await fixture.Plan(backup, true);
        UninstallReceipt? final = null;
        fixture.PurgeCheckpoint = name => { if (name == "secrets") final = UninstallReceipt.Load(fixture.Root); };
        fixture.Runner.Before = args =>
        {
            if (args is ["volume", "rm", _])
            {
                Assert.Equal(UninstallPhase.RuntimeRemoved, UninstallReceipt.Load(fixture.Root)!.Phase);
                Assert.DoesNotContain(fixture.Runner.Containers.Values, c => c.GetProperty("Name").GetString()!.Contains(fixture.Config.Project));
                Assert.DoesNotContain(fixture.Runner.Networks.Keys, n => n.StartsWith(fixture.Config.Project));
            }
            if (args.Contains("backup-worker") && args.Contains("run")) Assert.Null(UninstallReceipt.Load(fixture.Root));
        };
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(UninstallPhase.VolumesRemoved, final!.Phase);
        Assert.Equal(plan.Resources.Count(r => r.Kind == UninstallResourceKind.Volume && r.DockerId is not null), final.RemovedVolumes.Length);
        if (backup)
        {
            Assert.Equal(fixture.Runner.Captured!.Archive, final.FinalBackup!.Archive);
            var archive = Path.Combine(fixture.Config.Backup.Destination, final.FinalBackup.Basename);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))), final.FinalBackup.Sha256);
            Assert.Contains(fixture.Runner.Calls, call => call.Contains("backup-reader") && call[^1] == final.FinalBackup.Basename);
        }
        else { Assert.Null(final.FinalBackup); Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("run")); }
        Assert.Equal(markerBytes, File.ReadAllBytes(marker));
        Assert.Equal("old recovery bytes", File.ReadAllText(oldArchive));
        Assert.Equal(identity.Inode, destination.Identity.Inode);
        Assert.Equal(identity.Mode, destination.Identity.Mode);
        Assert.Equal(identity.User, destination.Identity.User);
        Assert.Equal(new[] { "operation.lock", "releases", "uninstall-purged.json" }, Directory.GetFileSystemEntries(fixture.Root).Select(Path.GetFileName).Order());
        Assert.True(fixture.Runner.Containers.ContainsKey("sentinel"));
        Assert.True(fixture.Runner.Networks.ContainsKey("sentinel-network"));
        Assert.DoesNotContain("sentinel-volume", fixture.Runner.MissingVolumes);
        Assert.DoesNotContain(fixture.Runner.Calls, c => c.Contains("prune") || c.Contains("down") || c is ["image", "rm", ..]);
        Assert.All(fixture.Runner.Calls.Where(c => c is ["volume", "rm", _]), c =>
            Assert.Contains(plan.Resources, r => r.Kind == UninstallResourceKind.Volume && r.Action == UninstallAction.Remove && r.DockerId == c[^1]));
    }

    /// <summary>Capture and exact verification failures still precede purge receipt creation and every destructive mutation.</summary>
    [Theory]
    [InlineData("backup")]
    [InlineData("verify")]
    [InlineData("integrity")]
    [InlineData("compatibility")]
    [InlineData("uuid")]
    [InlineData("name")]
    [InlineData("publication")]
    public async Task FailedBackupCannotAuthorizePurge(string failure)
    {
        using var fixture = new UninstallCommandFixture(true);
        var plan = await fixture.Plan(true, true);
        fixture.Runner.Failure = failure is "backup" or "verify" ? failure : null;
        fixture.Runner.InvalidIntegrity = failure == "integrity";
        fixture.Runner.InvalidCompatibility = failure == "compatibility";
        fixture.Runner.WrongArchive = failure == "uuid";
        fixture.Runner.WrongName = failure == "name";
        fixture.Runner.ExtraArchive = failure == "publication";
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Null(UninstallReceipt.Load(fixture.Root));
        Assert.DoesNotContain(fixture.Runner.Calls, c => c[0] == "update" || c is ["volume" or "network", "rm", ..]);
        Assert.Empty(fixture.Runner.MissingVolumes);
    }

    /// <summary>Valid terminal restore history contributes only its exact generated volume names, including an absent historical target.</summary>
    [Fact]
    public async Task PurgeDeletesReceiptedHistoricalGenerations()
    {
        using var fixture = new UninstallCommandFixture(true);
        var history = AddHistory(fixture);
        var plan = await fixture.Plan(purge: true);
        Assert.Contains(plan.Resources, r => r.Name == history.Volumes[0] && r.LifecycleOperation == history.Plan.Operation);
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.All(history.Volumes, name => Assert.Contains(fixture.Runner.Calls, c => c.SequenceEqual(new[] { "volume", "rm", name })));
    }

    /// <summary>Each exact absence is durable before another volume; replay reconciles a lost acknowledgement without recapture.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedVolumeDeletionPersistsProgress(bool lostAck)
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await fixture.Plan(purge: true);
        fixture.Runner.LostRemoval = lostAck ? "volume" : null;
        var attempts = 0;
        fixture.Runner.Before = c => { if (c is ["volume", "rm", _] && ++attempts == 2) throw new OperationCanceledException(); };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        var receipt = UninstallReceipt.Load(fixture.Root)!;
        Assert.Equal(UninstallPhase.RuntimeRemoved, receipt.Phase);
        Assert.Single(receipt.RemovedVolumes);
        Assert.Throws<UsageException>(() => receipt.Advance(UninstallPhase.VolumesRemoved).Validate(fixture.Root));
        File.Delete(UninstallPreparation.PathFor(fixture.Root, plan.Hash()));
        fixture.Runner.Before = null;
        fixture.Runner.Calls.Clear();
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.DoesNotContain(fixture.Runner.Calls, c => c is ["volume", "rm", _] && receipt.RemovedVolumes.Contains(c[^1]));
    }

    /// <summary>Deletion followed by a lost inspection/progress acknowledgement leaves RuntimeRemoved; replay records absence without deleting again.</summary>
    [Fact]
    public async Task UnrecordedDeletedVolumeReconcilesIndependentAbsence()
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await fixture.Plan(purge: true);
        fixture.Runner.Before = c =>
        {
            if (c is ["volume", "ls", ..] && fixture.Runner.MissingVolumes.Count == 1 &&
                UninstallReceipt.Load(fixture.Root) is { RemovedVolumes.Length: 0 }) throw new OperationCanceledException();
        };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Empty(UninstallReceipt.Load(fixture.Root)!.RemovedVolumes);
        var deleted = Assert.Single(fixture.Runner.MissingVolumes);
        fixture.Runner.Before = null;
        fixture.Runner.Calls.Clear();
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.DoesNotContain(fixture.Runner.Calls, c => c.SequenceEqual(new[] { "volume", "rm", deleted }));
    }

    /// <summary>Even successful exact capture/verification cannot authorize purge after protected environment authority changed during the backup.</summary>
    [Fact]
    public async Task PostBackupRevalidationPrecedesPurgeAuthorization()
    {
        using var fixture = new UninstallCommandFixture(true);
        var plan = await fixture.Plan(true, true);
        fixture.Runner.Before = c =>
        {
            if (c.Contains("backup-reader") && c.Contains("run")) File.AppendAllText(Path.Combine(fixture.Root, "deployment.env"), "\n");
        };
        Assert.Equal(2, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.NotNull(fixture.Runner.Captured);
        Assert.Null(UninstallReceipt.Load(fixture.Root));
        Assert.Empty(fixture.Runner.MissingVolumes);
        Assert.DoesNotContain(fixture.Runner.Calls, c => c[0] == "update" || c is ["volume" or "network", "rm", ..]);
    }

    /// <summary>Changed data inode, a previously removed name, unexpected historical creation and foreign project residue never become targets.</summary>
    [Theory]
    [InlineData("inode")]
    [InlineData("removed")]
    [InlineData("absent-history")]
    [InlineData("unreceipted")]
    public async Task ReplacedOrUnacceptedVolumesRefuse(string change)
    {
        using var fixture = new UninstallCommandFixture(true);
        var history = change == "absent-history" ? AddHistory(fixture, false) : null;
        var plan = await fixture.Plan(purge: true);
        var attempts = 0;
        fixture.Runner.Before = c => { if (c is ["volume", "rm", _] && ++attempts == 2) throw new OperationCanceledException(); };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        var receipt = UninstallReceipt.Load(fixture.Root)!;
        var name = change == "removed" ? receipt.RemovedVolumes[0] : change == "absent-history" ? history!.Volumes[0] :
            plan.Resources.First(r => r.Kind == UninstallResourceKind.Volume && !receipt.RemovedVolumes.Contains(r.Name)).Name;
        if (change == "unreceipted")
        {
            var volume = UninstallPlanningTests.Volume(fixture.Config, "app-data", "_unreceipted");
            name = volume.GetProperty("Name").GetString()!;
            fixture.Runner.AdditionalVolumes.Add(name, volume);
        }
        if (change == "removed") fixture.Runner.MissingVolumes.Remove(name);
        var path = Path.Combine(fixture.Runner.DockerRoot, "volumes", name, "_data");
        if (Directory.Exists(path)) Directory.Move(path, path + "-old");
        Directory.CreateDirectory(path);
        if (change == "absent-history") fixture.Runner.MissingVolumes.Remove(name);
        fixture.Runner.Before = null;
        fixture.Runner.Calls.Clear();
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.DoesNotContain(fixture.Runner.Calls, c => c is ["volume", "rm", _]);
        Assert.Equal(receipt.RemovedVolumes, UninstallReceipt.Load(fixture.Root)!.RemovedVolumes);
    }

    /// <summary>The last independent consumer query can refuse deletion even after full inventory observation; no force or consumer removal follows.</summary>
    [Fact]
    public async Task RemainingConsumerBlocksExactVolumeDeletion()
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await fixture.Plan(purge: true);
        var name = plan.Resources.First(r => r.Kind == UninstallResourceKind.Volume).Name;
        var observed = 0;
        fixture.Runner.Before = c =>
        {
            if (c is ["ps", "-aq", "--no-trunc", "--filter", var filter] && filter == "volume=" + name &&
                UninstallReceipt.Load(fixture.Root)?.Phase == UninstallPhase.RuntimeRemoved && ++observed == 2)
                AddSentinelContainer(fixture, name);
        };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(UninstallPhase.RuntimeRemoved, UninstallReceipt.Load(fixture.Root)!.Phase);
        Assert.Empty(UninstallReceipt.Load(fixture.Root)!.RemovedVolumes);
        Assert.True(fixture.Runner.Containers.ContainsKey("sentinel"));
        Assert.DoesNotContain(fixture.Runner.Calls, c => c is ["volume", "rm", _]);
    }

    /// <summary>Construct valid terminal restore authority through existing receipt persistence and real fake Docker data directories.</summary>
    internal static RestoreReceipt AddHistory(UninstallCommandFixture fixture, bool present = true)
    {
        var config = fixture.Config;
        var plan = new RestorePlan(Guid.NewGuid(), fixture.Root, config, config.Installation, Guid.NewGuid(), new string('a', 64),
            DateTimeOffset.UnixEpoch, "quiesced", "bundle", "capture", "restore", config.StorageGeneration, Guid.NewGuid().ToString("N"),
            false, true, false) { OperatorOwner = config.Release };
        var candidate = RestoreCandidate.Configuration(plan);
        var volumes = new[] { "db-data", "app-data", "app-cache" }.Select(role => UninstallPlanningTests.Volume(candidate, role)).ToArray();
        var receipt = new RestoreReceipt { Plan = plan, PlanHash = plan.Hash(), Phase = RestorePhase.Aborted,
            Volumes = volumes.Select(v => v.GetProperty("Name").GetString()!).ToArray() };
        receipt.Save(fixture.Root);
        foreach (var volume in volumes)
        {
            var name = volume.GetProperty("Name").GetString()!;
            fixture.Runner.AdditionalVolumes.Add(name, volume);
            if (present) Directory.CreateDirectory(Path.Combine(fixture.Runner.DockerRoot, "volumes", name, "_data"));
            else fixture.Runner.MissingVolumes.Add(name);
        }
        return receipt;
    }

    /// <summary>Unrelated sentinel resources are visible to global discovery but never share installation labels, names or consumers.</summary>
    private static void AddSentinels(UninstallCommandFixture fixture)
    {
        AddSentinelContainer(fixture, null);
        fixture.Runner.Networks.Add("sentinel-network", JsonSerializer.SerializeToElement(new { Name = "sentinel-network", Id = new string('c', 64), Labels = new { } }));
        fixture.Runner.AdditionalVolumes.Add("sentinel-volume", JsonSerializer.SerializeToElement(new { Name = "sentinel-volume", Labels = new { } }));
    }

    /// <summary>A foreign stopped consumer is deliberately never adopted, fenced or force-removed.</summary>
    private static void AddSentinelContainer(UninstallCommandFixture fixture, string? volume)
    {
        fixture.Runner.Containers["sentinel"] = JsonSerializer.SerializeToElement(new { Name = "/sentinel", Id = new string('d', 64),
            Config = new { Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "foreign" } },
            State = new { Running = false }, Mounts = volume is null ? Array.Empty<object>() : new object[] { new { Type = "volume", Name = volume } },
            NetworkSettings = new { Networks = new { } } });
    }
}
