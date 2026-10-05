using System.Runtime.InteropServices;
using System.Text.Json;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Real protected temporary trees prove that receipt-owned cleanup stays bounded and never follows administrator paths.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class UninstallPurgeRootTests
{
    /// <summary>Create a real hard-link counterexample at the protected deletion seam.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int link(string existing, string name);
    /// <summary>Assign actual shipped consumer identities and construct unsafe ownership counterexamples.</summary>
    [DllImport("libc", SetLastError = true)]
    private static extern int chown(string name, uint user, uint group);

    /// <summary>Failed setup staging and unknown release-cache entries cannot survive as supposedly immutable terminal cache.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NonReleaseCacheResidueRefuses(bool terminal)
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await fixture.Plan(purge: true);
        if (terminal) Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        var stage = Path.Combine(fixture.Root, "releases", ".setup-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage, ProtectedFiles.PrivateDirectory);
        ProtectedFiles.Create(Path.Combine(stage, "admin-password"), "unpublished setup secret");
        fixture.Runner.Calls.Clear();
        Assert.NotEqual(0, await fixture.Command(terminal ? ["doctor"] : ["uninstall", "--accept-plan", plan.Hash()]));
        Assert.Equal("unpublished setup secret", File.ReadAllText(Path.Combine(stage, "admin-password")));
        Assert.Empty(fixture.Runner.Calls);
        Assert.Null(UninstallReceipt.Load(fixture.Root));
    }

    /// <summary>Receipted incomplete import bytes remain untouched alongside all validated immutable release payloads.</summary>
    [Fact]
    public async Task ValidReleaseImportStagingIsRetained()
    {
        using var fixture = new UninstallCommandFixture();
        var releases = Path.Combine(fixture.Root, "releases");
        var name = ".stage-" + Guid.NewGuid().ToString("N");
        ProtectedFiles.Create(Path.Combine(releases, name + ".json"), JsonSerializer.Serialize(fixture.Config.Release));
        Directory.CreateDirectory(Path.Combine(releases, name), ProtectedFiles.PrivateDirectory);
        ProtectedFiles.Create(Path.Combine(releases, name, "compose.yaml"), "partial release copy");
        var plan = await fixture.Plan(purge: true);
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal("partial release copy", File.ReadAllText(Path.Combine(releases, name, "compose.yaml")));
        Assert.Equal(0, await fixture.Command("doctor"));
        Assert.Equal(fixture.Config.Release, ReleaseAuthority.From(ReleaseStore.Select(fixture.Root, fixture.Config.Release!)));
    }

    /// <summary>Unpublished same-mount receipt staging cannot introduce unknown top-level residue or become a second lifecycle owner.</summary>
    [Fact]
    public async Task InterruptedReceiptStagingRemainsPrivateAndReplayable()
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await fixture.Plan(purge: true);
        fixture.Runner.Before = c =>
        {
            if (c[0] != "update") return;
            var receipt = UninstallReceipt.Load(fixture.Root)!;
            var staged = Path.Combine(fixture.Root, "uninstall-plans", ".receipt-" + Guid.NewGuid().ToString("N") + ".json");
            ProtectedFiles.Create(staged, JsonSerializer.Serialize(receipt.Advance(UninstallPhase.Fenced)));
            throw new OperationCanceledException();
        };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(UninstallPhase.Authorized, UninstallReceipt.Load(fixture.Root)!.Phase);
        fixture.Runner.Before = null;
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(UninstallState.Purged, UninstallReceipt.State(fixture.Root));
    }

    /// <summary>Durable VolumesRemoved survives deletion of config, secrets and recovery exclusion; replay never rebuilds those authorities or calls Docker.</summary>
    [Theory]
    [InlineData("secrets")]
    [InlineData("recovery-control")]
    [InlineData("installation.json")]
    [InlineData("deployment.env")]
    public async Task PartialCleanupReplaysWithoutDeletedAuthority(string point)
    {
        using var fixture = new UninstallCommandFixture(true);
        var plan = await fixture.Plan(purge: true);
        fixture.PurgeCheckpoint = name => { if (name == point) throw new IOException("cleanup interrupted"); };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.False(Path.Exists(Path.Combine(fixture.Root, point)));
        var receipt = UninstallReceipt.Load(fixture.Root)!;
        Assert.Equal(UninstallPhase.VolumesRemoved, receipt.Phase);
        Assert.True(File.Exists(UninstallReceipt.PathFor(fixture.Root)));
        Assert.False(File.Exists(Path.Combine(fixture.Root, UninstallPurgeTombstone.Name)));
        Assert.Equal(fixture.Config.Release, ReleaseAuthority.From(ReleaseDispatch.Select(fixture.Root, false)));
        fixture.PurgeCheckpoint = null;
        fixture.Runner.Calls.Clear();
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Empty(fixture.Runner.Calls);
        Assert.Equal(UninstallState.Purged, UninstallReceipt.State(fixture.Root));
        Assert.True(Directory.Exists(fixture.Config.Backup!.Destination));
    }

    /// <summary>All stable root artifacts and mixed consumer-owned trees are removed; release bytes and the original lock inode survive.</summary>
    [Fact]
    public async Task CleanupRemovesExactProductOwnedTreesAndFiles()
    {
        using var fixture = new UninstallCommandFixture(true);
        var plan = await ReachVolumesRemoved(fixture);
        foreach (var name in new[] { "setup-progress.json", "restore-complete", "backup-identity", "backup-previous.json" })
            ProtectedFiles.Create(Path.Combine(fixture.Root, name), "retained terminal state");
        foreach (var name in new[] { "deployment-generations", "storage-generations", "update-plans", "uninstall-history" })
        {
            Directory.CreateDirectory(Path.Combine(fixture.Root, name), ProtectedFiles.PrivateDirectory);
            var path = Path.Combine(fixture.Root, name, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path, ProtectedFiles.PrivateDirectory);
            ProtectedFiles.Create(Path.Combine(path, "retained.json"), "owned bytes");
        }
        var restore = Path.Combine(fixture.Root, "restore-plans", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(restore, ProtectedFiles.PrivateDirectory);
        ProtectedFiles.Create(Path.Combine(restore, "source.json"), "source", 0, 1654);
        ProtectedFiles.Create(Path.Combine(restore, "bootstrap-secret-" + Guid.NewGuid().ToString("N")), "credential", 1654);
        var frozen = Path.Combine(restore, "frozen");
        Directory.CreateDirectory(frozen, (UnixFileMode)0x1e8);
        Assert.Equal(0, chown(frozen, 0, 1654));
        ProtectedFiles.Create(Path.Combine(frozen, "archive.tar"), "frozen", 0, 1654);
        var verified = Path.Combine(restore, "verified");
        Directory.CreateDirectory(verified, ProtectedFiles.PrivateDirectory);
        Assert.Equal(0, chown(verified, 1654, 1654));
        ProtectedFiles.Create(Path.Combine(verified, "manifest.json"), "verified", 1654);
        ProtectedFiles.Create(Path.Combine(fixture.Root, "recovery-control/state/scheduler.json"), "scheduler", 1654);
        using var directory = new SafeDirectory(fixture.Root);
        ulong lockInode;
        using (var operation = directory.Read("operation.lock")) lockInode = SafeDirectory.Inspect(operation.SafeFileHandle).Inode;
        var release = ReleaseStore.Select(fixture.Root, fixture.Config.Release!);
        fixture.PurgeCheckpoint = null;
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(new[] { "operation.lock", "releases", UninstallPurgeTombstone.Name }, directory.Names());
        using var retainedLock = directory.Read("operation.lock");
        Assert.Equal(lockInode, SafeDirectory.Inspect(retainedLock.SafeFileHandle).Inode);
        Assert.Equal(release.Fingerprint, ReleaseStore.Select(fixture.Root, fixture.Config.Release!).Fingerprint);
    }

    /// <summary>Links, hard links, unsafe mode/owner and wrong-type root artifacts stop forward cleanup without reaching terminal evidence.</summary>
    [Theory]
    [InlineData("symlink")]
    [InlineData("hardlink")]
    [InlineData("mode")]
    [InlineData("owner")]
    [InlineData("type")]
    public async Task UnsafeOwnedTreeEntryFailsClosed(string kind)
    {
        using var fixture = new UninstallCommandFixture(true);
        var plan = await ReachVolumesRemoved(fixture);
        var sentinel = Path.Combine(fixture.Config.Backup!.Destination, "administrator-file");
        ProtectedFiles.Create(sentinel, "must survive", 1654);
        var tree = Path.Combine(fixture.Root, "update-plans", "retained");
        Directory.CreateDirectory(tree, ProtectedFiles.PrivateDirectory);
        var path = Path.Combine(tree, "unsafe");
        if (kind == "symlink") File.CreateSymbolicLink(path, sentinel);
        else if (kind == "hardlink") Assert.Equal(0, link(sentinel, path));
        else if (kind == "type") Directory.CreateDirectory(Path.Combine(fixture.Root, "backup-previous.json"), ProtectedFiles.PrivateDirectory);
        else
        {
            ProtectedFiles.Create(path, "unsafe");
            if (kind == "mode") File.SetUnixFileMode(path, ProtectedFiles.PrivateFile | UnixFileMode.GroupRead);
            else Assert.Equal(0, chown(path, 1654, 1654));
        }
        fixture.PurgeCheckpoint = null;
        fixture.Runner.Calls.Clear();
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal("must survive", File.ReadAllText(sentinel));
        Assert.Equal(UninstallPhase.VolumesRemoved, UninstallReceipt.Load(fixture.Root)!.Phase);
        Assert.False(File.Exists(Path.Combine(fixture.Root, UninstallPurgeTombstone.Name)));
        Assert.Empty(fixture.Runner.Calls);
    }

    /// <summary>Top-level administrator residue is never inferred to be installation ownership before or after destructive authorization.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnknownRootEntryPreventsTerminalPurge(bool authorized)
    {
        using var fixture = new UninstallCommandFixture();
        var plan = authorized ? await ReachVolumesRemoved(fixture) : await fixture.Plan(purge: true);
        ProtectedFiles.Create(Path.Combine(fixture.Root, "administrator-notes"), "keep");
        fixture.PurgeCheckpoint = null;
        fixture.Runner.Calls.Clear();
        Assert.NotEqual(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.Root, "administrator-notes")));
        Assert.False(File.Exists(Path.Combine(fixture.Root, UninstallPurgeTombstone.Name)));
        Assert.Empty(fixture.Runner.Calls);
        Assert.Equal(authorized, UninstallReceipt.Load(fixture.Root) is not null);
    }

    /// <summary>Unresolved installation pointers and provisioning state cannot reach purge authorization even when their names look familiar.</summary>
    [Theory]
    [InlineData("installation.backup-next")]
    [InlineData("installation.json.abort")]
    [InlineData("installation.update-deadbeef")]
    [InlineData("setup-provisioning")]
    public async Task TransientRootAuthorityRefusesBeforeAuthorization(string name)
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await fixture.Plan(purge: true);
        ProtectedFiles.Create(Path.Combine(fixture.Root, name), "uncommitted transition");
        fixture.Runner.Calls.Clear();
        Assert.NotEqual(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Null(UninstallReceipt.Load(fixture.Root));
        Assert.Empty(fixture.Runner.MissingVolumes);
        Assert.Equal("uncommitted transition", File.ReadAllText(Path.Combine(fixture.Root, name)));
    }

    /// <summary>The same primitive used by purge deterministically refuses a nested procfs mount and a changed child directory identity.</summary>
    [Fact]
    public void VerifiedChildRemovalRefusesMountCrossingAndReplacement()
    {
        using var hostRoot = new SafeDirectory("/");
        using var proc = new SafeDirectory("/proc");
        Assert.Throws<IOException>(() => hostRoot.Child("proc"));
        Assert.Throws<IOException>(() => hostRoot.RemoveChild("proc", proc));
        using var fixture = new UninstallCommandFixture();
        var path = Path.Combine(fixture.Root, "update-plans");
        Directory.CreateDirectory(path, ProtectedFiles.PrivateDirectory);
        using var root = new SafeDirectory(fixture.Root);
        using var old = root.Child("update-plans");
        Directory.Move(path, path + "-old");
        Directory.CreateDirectory(path, ProtectedFiles.PrivateDirectory);
        Assert.Throws<IOException>(() => root.RemoveChild("update-plans", old));
        Assert.True(Directory.Exists(path));
    }

    /// <summary>Reach the real durable cutoff and interrupt at its first root deletion; no artificial terminal receipt is constructed.</summary>
    internal static async Task<UninstallPlan> ReachVolumesRemoved(UninstallCommandFixture fixture)
    {
        var plan = await fixture.Plan(purge: true);
        fixture.PurgeCheckpoint = name => { if (name == "secrets") throw new IOException("pause root cleanup"); };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(UninstallPhase.VolumesRemoved, UninstallReceipt.Load(fixture.Root)!.Phase);
        return plan;
    }
}
