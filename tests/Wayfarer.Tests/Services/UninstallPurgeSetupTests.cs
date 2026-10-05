using System.Reflection;
using System.Text.Json;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Fresh setup consumes terminal purge evidence only at publication and generates independent installation inputs.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class UninstallPurgeSetupTests
{
    /// <summary>Early read-only preparation and password cancellation retain the tombstone; the later locked publication creates new secrets and no backup policy.</summary>
    [Fact]
    public async Task FreshSetupConsumesOnlyAtOrdinaryPublication()
    {
        using var fixture = new UninstallCommandFixture(true);
        var oldFingerprint = ProtectedFiles.SecretsFingerprint(fixture.Root);
        await UninstallPurgeTombstoneTests.Purge(fixture);
        var tombstone = UninstallPurgeTombstone.Load(fixture.Root)!;
        var marker = Path.Combine(fixture.Config.Backup!.Destination, ".wayfarer-recovery");
        var oldMarker = File.ReadAllBytes(marker);
        var bundle = ReleaseStore.Select(fixture.Root, tombstone.OperatorOwner);
        Setup.RequireFreshState(fixture.Root, allowPurged: true);
        Assert.Throws<UsageException>(() => Setup.RequireFreshState(fixture.Root));
        var choices = new[] { "--hostname", "new.wayfarer.example.org", "--mode", "external", "--loopback-port",
            fixture.Config.LoopbackPort.ToString(), "--project", "new-wayfarer", "--edge-prefix", "172.31.240" };
        var process = new FreshProcess(fixture.Config);
        var terminal = new SetupTerminal { CancelPassword = true };
        var acquisitions = 0;
        var observedPublication = false;
        var setup = new Setup(process, terminal, (root, _, _) =>
        {
            Assert.Equal(tombstone, UninstallPurgeTombstone.Load(root));
            Assert.False(Setup.HasProtectedState(root));
            acquisitions++;
            return Task.FromResult(bundle);
        })
        {
            ProvisioningCheckpoint = path =>
            {
                if (Path.GetFileName(path) != "installation.json") return;
                Assert.Null(UninstallPurgeTombstone.Load(fixture.Root));
                Assert.True(Directory.Exists(bundle.Directory));
                observedPublication = true;
            }
        };
        Assert.Equal(1, await setup.RunAsync(fixture.Root, choices, default));
        Assert.Equal(tombstone, UninstallPurgeTombstone.Load(fixture.Root));
        Assert.False(observedPublication);
        terminal.CancelPassword = false;
        Assert.Equal(1, await setup.RunAsync(fixture.Root, choices, default)); // Intentional first Compose mutation failure stops at the focused publication seam.
        Assert.Equal(2, acquisitions);
        Assert.True(observedPublication);
        Assert.True(process.ReachedExecution);
        var fresh = Deployment.Load(fixture.Root);
        Assert.Equal(Guid.Empty, fresh.Installation);
        Assert.Null(fresh.Backup);
        Assert.Null(fresh.StorageGeneration);
        Assert.Equal("new.wayfarer.example.org", fresh.Hostname);
        Assert.NotEqual(oldFingerprint, ProtectedFiles.SecretsFingerprint(fixture.Root));
        Assert.Equal(bundle.Fingerprint, ReleaseStore.Select(fixture.Root, fresh.Release!).Fingerprint);
        Assert.Equal(oldMarker, File.ReadAllBytes(marker));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "recovery-control")));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "uninstall-history")));
        Assert.Null(UninstallPurgeTombstone.Load(fixture.Root));
        Assert.True(Setup.HasProtectedState(fixture.Root));
        Assert.Throws<UsageException>(() => Setup.RequireFreshState(fixture.Root));
    }

    /// <summary>Fresh setup rejects changed terminal evidence observed during acquisition, before consuming it or generating new credentials.</summary>
    [Fact]
    public async Task FreshPublicationRevalidatesOriginalTombstone()
    {
        using var fixture = new UninstallCommandFixture();
        await UninstallPurgeTombstoneTests.Purge(fixture);
        var tombstone = UninstallPurgeTombstone.Load(fixture.Root)!;
        var changed = tombstone with { Operation = Guid.NewGuid() };
        var setup = new Setup(new FreshProcess(fixture.Config), new SetupTerminal(), (root, _, _) =>
        {
            File.WriteAllText(Path.Combine(root, UninstallPurgeTombstone.Name), JsonSerializer.Serialize(changed));
            return Task.FromResult(ReleaseStore.Select(root, changed.OperatorOwner));
        });
        Assert.Equal(2, await setup.RunAsync(fixture.Root, ["--hostname", "new.wayfarer.example.org", "--mode", "external",
            "--loopback-port", fixture.Config.LoopbackPort.ToString()], default));
        Assert.Equal(changed, UninstallPurgeTombstone.Load(fixture.Root));
        Assert.False(Setup.HasProtectedState(fixture.Root));
    }

    /// <summary>The CLI allows plain setup through its own native prerequisites, while resume refuses before even Docker inspection.</summary>
    [Fact]
    public async Task CliAllowsFreshSetupButNeverPurgeResume()
    {
        using var fixture = new UninstallCommandFixture();
        await UninstallPurgeTombstoneTests.Purge(fixture);
        fixture.Runner.Calls.Clear();
        fixture.Runner.Failure = "info";
        Assert.NotEqual(0, await fixture.Command("setup"));
        Assert.Contains(fixture.Runner.Calls, c => c[0] == "info");
        Assert.Equal(UninstallState.Purged, UninstallReceipt.State(fixture.Root));
        fixture.Runner.Calls.Clear();
        Assert.Equal(2, await fixture.Command("setup", "--resume"));
        Assert.Empty(fixture.Runner.Calls);
        await Assert.ThrowsAsync<UsageException>(() => new Setup(fixture.Runner, fixture.Terminal).RunAsync(fixture.Root, ["--resume"], default));
    }

    /// <summary>A newly generated backup identity cannot adopt the old marker at the existing destination ownership boundary.</summary>
    [Fact]
    public async Task OldBackupMarkerContinuesToRefuseNewInstallation()
    {
        using var fixture = new UninstallCommandFixture(true);
        var marker = Path.Combine(fixture.Config.Backup!.Destination, ".wayfarer-recovery");
        var bytes = File.ReadAllBytes(marker);
        using var directory = new SafeDirectory(fixture.Config.Backup.Destination);
        var identity = directory.Identity;
        await UninstallPurgeTombstoneTests.Purge(fixture);
        var fresh = fixture.Config with { Installation = Guid.Empty, Backup = null };
        var ownership = typeof(BackupConfiguration).GetMethod("Destination", BindingFlags.NonPublic | BindingFlags.Static)!;
        var failure = Assert.Throws<TargetInvocationException>(() => ownership.Invoke(null,
            [fixture.Root, fresh, fixture.Config.Backup, Guid.NewGuid()]));
        Assert.IsType<UsageException>(failure.InnerException);
        Assert.Contains("another installation", failure.InnerException!.Message);
        Assert.Equal(bytes, File.ReadAllBytes(marker));
        Assert.Equal(identity.Inode, directory.Identity.Inode);
        Assert.Equal(identity.User, directory.Identity.User);
        Assert.Equal(identity.Mode, directory.Identity.Mode);
    }

    /// <summary>Reuse the standard setup process boundary: read-only prerequisites succeed and the first ordinary execution stops.</summary>
    private sealed class FreshProcess(Deployment config) : IProcessRunner
    {
        /// <summary>Publication must be complete before the first canonical Compose mutation is attempted.</summary>
        internal bool ReachedExecution { get; private set; }

        /// <summary>No real Docker command runs; literal image/platform resolution remains checked by the product preflight.</summary>
        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken token, Action<string>? output = null)
        {
            if (args.Contains("--quiet")) { ReachedExecution = true; return Task.FromResult(new ProcessResult(1, "stop at publication seam")); }
            var value = args[0] == "info" ? config.RuntimePlatform : args.Contains("version") ? "2.24.4" : args.Contains("json")
                ? JsonSerializer.Serialize(new { services = new
                {
                    wayfarer = new { image = "ghcr.io/stef-k/wayfarer@" + config.AppDigest, platform = config.RuntimePlatform },
                    db = new { image = "ghcr.io/stef-k/wayfarer-db@" + config.DbDigest, platform = config.RuntimePlatform }
                } }) : "";
            return Task.FromResult(new ProcessResult(0, value));
        }
    }

    /// <summary>Protected password input may cancel after preparation; it never alters terminal purge evidence itself.</summary>
    private sealed class SetupTerminal : ITerminal
    {
        /// <summary>Explicit options avoid unrelated interactive setup choices.</summary>
        public bool Interactive => false;
        /// <summary>Interrupt after read-only preparation and before publication to prove tombstone retention.</summary>
        internal bool CancelPassword { get; set; }
        /// <summary>These cases assert protected state rather than presentation output.</summary>
        public void Write(string message) { }
        /// <summary>Expected first-execution failure is observed through its return code and protected setup authority.</summary>
        public void Error(string message) { }
        /// <summary>Supplied options must satisfy setup without an implicit interactive answer.</summary>
        public string? Read(string prompt) => throw new InvalidOperationException("Unexpected prompt.");
        /// <summary>Provide fresh credentials or deliberately cancel before the host-locked publication boundary.</summary>
        public string Password(bool fromStdin) => CancelPassword ? throw new OperationCanceledException() : "Fresh-password-Only123!";
    }
}
