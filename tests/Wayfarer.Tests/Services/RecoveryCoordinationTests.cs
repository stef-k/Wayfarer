using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Wayfarer.Tests.Infrastructure;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Recovery coordination exercises real protected Linux files in one owned temporary installation.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class RecoveryCoordinationTests : IDisposable
{
    private readonly TestDirectory fixture = new();
    private readonly Deployment config;
    private string Root => fixture.Path;
    private string Reservation => Path.Combine(Root, "recovery-control/host-operation.json");

    [DllImport("libc", SetLastError = true)]
    private static extern int link(string source, string destination);

    /// <summary>Provision the existing control contract without selecting a host installation or Docker resource.</summary>
    public RecoveryCoordinationTests()
    {
        ProtectedFiles.RequireRoot();
        File.SetUnixFileMode(Root, ProtectedFiles.PrivateDirectory);
        config = new Deployment { Schema = 2, Installation = Guid.NewGuid(), Bundle = Path.Combine(Root, "bundle"),
            Hostname = "wayfarer.example.org", Mode = "external", AppDigest = "sha256:" + new string('a', 64) };
        ProtectedFiles.Create(Path.Combine(Root, "installation.json"), JsonSerializer.Serialize(config));
        ProtectedFiles.Create(Path.Combine(Root, "deployment.env"), config.EnvironmentFile(Root));
        ProtectedFiles.CreateSecrets(Root);
        ProtectedFiles.Create(Path.Combine(Root, "setup-complete"), "");
        BackupConfiguration.ProvisionControl(Root, bootstrap: true);
    }

    /// <summary>The actual reservation writer's root:1654/0640 output reaches exact stop/wait/removal and durable cleanup.</summary>
    [Fact]
    public async Task GeneratedDelegatedCaptureCanBeReconciled()
    {
        var receipt = Update();
        var reservation = Reserve(receipt);
        var runner = new CaptureRunner(Root, receipt, reservation.Token);
        await ReconcileCapture(receipt, runner);
        Assert.Equal(new[] { "ps", "inspect", "stop", "wait", "rm" }, runner.Calls);
        Assert.False(File.Exists(Reservation));
    }

    /// <summary>Foreign permission, link and parent states remain untouched before Docker can be consulted.</summary>
    [Theory]
    [InlineData("owner")]
    [InlineData("group")]
    [InlineData("mode")]
    [InlineData("hard-link")]
    [InlineData("symbolic-link")]
    [InlineData("parent")]
    public async Task UnsafeDelegatedCaptureIsRefused(string state)
    {
        var receipt = Update();
        var reservation = Reserve(receipt);
        if (state is "owner" or "group")
        {
            File.Delete(Reservation);
            ProtectedFiles.Create(Reservation, JsonSerializer.Serialize(reservation), state == "owner" ? 1654u : 0,
                state == "owner" ? 1654u : 0);
        }
        else if (state == "mode") File.SetUnixFileMode(Reservation, ProtectedFiles.PrivateFile);
        else if (state == "hard-link") Assert.Equal(0, link(Reservation, Reservation + ".alias"));
        else if (state == "symbolic-link")
        {
            File.Move(Reservation, Reservation + ".owned");
            File.CreateSymbolicLink(Reservation, Reservation + ".owned");
        }
        else File.SetUnixFileMode(Path.GetDirectoryName(Reservation)!, (UnixFileMode)509); // Unsafe 0775 parent.
        var original = File.ReadAllBytes(Reservation);
        var runner = new CaptureRunner(Root, receipt, reservation.Token);
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => ReconcileCapture(receipt, runner));
            Assert.Empty(runner.Calls);
            Assert.Equal(original, File.ReadAllBytes(Reservation));
        }
        finally
        {
            if (state == "symbolic-link") File.Delete(Reservation);
        }
    }

    /// <summary>Schema, token, hold and phase facts cannot grant another operation's capture authority.</summary>
    [Theory]
    [InlineData("token")]
    [InlineData("container")]
    [InlineData("hold")]
    [InlineData("phase")]
    [InlineData("duplicate")]
    public async Task ForeignDelegatedCaptureIsRefused(string state)
    {
        var receipt = Update();
        var reservation = new HostRecoveryOperation(1, Guid.NewGuid().ToString("N"), receipt.CaptureContainer!, true, UpdateHold: true);
        if (state == "token") reservation = reservation with { Token = "foreign" };
        if (state == "container") reservation = reservation with { Container = "another-backup-" + Guid.NewGuid().ToString("N") };
        if (state == "hold") reservation = reservation with { RestoreHold = true };
        if (state == "phase") receipt = receipt with { Phase = UpdatePhase.Authorized };
        var content = JsonSerializer.Serialize(reservation);
        if (state == "duplicate") content = content.Insert(1, "\"Schema\":2,");
        ProtectedFiles.Create(Reservation, content, 0, 1654);
        var runner = new CaptureRunner(Root, receipt, reservation.Token);
        await Assert.ThrowsAnyAsync<Exception>(() => ReconcileCapture(receipt, runner));
        Assert.Empty(runner.Calls);
        Assert.Equal(content, File.ReadAllText(Reservation));
    }

    /// <summary>A different worker token or an unknown Docker stop/wait result never clears the reservation.</summary>
    [Theory]
    [InlineData("token")]
    [InlineData("stop")]
    [InlineData("wait")]
    public async Task UncertainCaptureWorkerRetainsReservation(string failure)
    {
        var receipt = Update();
        var reservation = Reserve(receipt);
        var original = File.ReadAllBytes(Reservation);
        var runner = new CaptureRunner(Root, receipt, failure == "token" ? Guid.NewGuid().ToString("N") : reservation.Token, failure);
        await Assert.ThrowsAnyAsync<Exception>(() => ReconcileCapture(receipt, runner));
        Assert.DoesNotContain("rm", runner.Calls);
        Assert.Equal(original, File.ReadAllBytes(Reservation));
    }

    /// <summary>Use the unchanged private coordinator seam so tests never launch the wider update lifecycle.</summary>
    private Task ReconcileCapture(UpdateReceipt receipt, CaptureRunner runner) => (Task)typeof(UpdateCommands)
        .GetMethod("ReconcileCaptureAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
        .Invoke(new UpdateCommands(runner, new Terminal()), [Root, receipt, CancellationToken.None])!;

    /// <summary>Generate reservation bytes and ownership through the production writer's exact API.</summary>
    private HostRecoveryOperation Reserve(UpdateReceipt receipt)
    {
        var reservation = new HostRecoveryOperation(1, Guid.NewGuid().ToString("N"), receipt.CaptureContainer!, true, UpdateHold: true);
        ProtectedFiles.Create(Reservation, JsonSerializer.Serialize(reservation), 0, 1654);
        return reservation;
    }

    /// <summary>Construct bounded valid update authority without importing or executing any release.</summary>
    private UpdateReceipt Update()
    {
        var release = new ReleaseAuthority("v1.9.19", new string('a', 64), "1.9.19", new string('b', 64));
        var current = config with { Schema = 4, Release = release, Backup = new BackupPolicy
        {
            Destination = Path.Combine(Root, "destination"), Payload = Path.Combine(config.Bundle, "wayfarer-recovery"),
            PayloadSha256 = new string('a', 64), Generation = new string('b', 64), Uploads = "uploads", Ring = "data-protection",
            Source = RecoveryCompatibilityTests.Source(3) with { Project = config.Project, Platform = NativePlatform.Current }
        } };
        var plan = new UpdatePlan(Guid.NewGuid(), Root, current, current, release,
            new ReleaseSourceBoundary("1.9.19", release.Fingerprint, current.Backup.Source.ExpectedMigrations[^1],
                true, false, "forward-inspection-or-recovery", ""), [], JsonSerializer.Serialize(current),
            current.EnvironmentFile(Root), new string('c', 64), new UpdateCapacity(1, 2, 2, 2, 0, 0))
            { TargetMigrations = current.Backup.Source.ExpectedMigrations };
        return new UpdateReceipt { Plan = plan, PlanHash = plan.Hash(), Phase = UpdatePhase.Fenced,
            CaptureContainer = config.Project + "-backup-" + Guid.NewGuid().ToString("N") };
    }

    /// <summary>Return exact generated Compose identity and record only the focused worker reconciliation sequence.</summary>
    private sealed class CaptureRunner(string root, UpdateReceipt receipt, string token, string? failure = null) : IProcessRunner
    {
        public List<string> Calls { get; } = [];
        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
        {
            Calls.Add(args[0]);
            var current = receipt.Plan.Current;
            var output = args[0] switch
            {
                "ps" => receipt.CaptureContainer!,
                "inspect" => JsonSerializer.Serialize(new[] { new
                {
                    Config = new { Image = "ghcr.io/stef-k/wayfarer-db@" + current.DbDigest,
                        Cmd = new[] { "backup", "--host-operation", token }, Labels = new Dictionary<string, string>
                        {
                            ["com.docker.compose.project"] = current.Project,
                            ["com.docker.compose.service"] = "backup-worker",
                            ["com.docker.compose.project.working_dir"] = current.Bundle,
                            ["com.docker.compose.project.config_files"] = Path.Combine(current.Bundle, "compose.yaml") + "," +
                                Path.Combine(current.Bundle, "external.yaml") + "," + Path.Combine(BackupCompose.DirectoryPath(root, current.Backup!), "compose.json")
                        } }, State = new { Status = "running" }
                } }),
                _ => "0"
            };
            return Task.FromResult(new ProcessResult(args[0] == failure ? 1 : 0, output));
        }
    }

    /// <summary>Reclaim only this test's private installation and credentials.</summary>
    public void Dispose() => fixture.Dispose();
}
