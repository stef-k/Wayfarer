using System.Security.Cryptography;
using System.Text.Json;
using WayfarerCtl;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Normal uninstall is exercised through CLI, protected receipt files and the existing Docker process seam.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class UninstallCommandsTests
{
    /// <summary>A valid CLI command reaches planning; redirected omission fails without prompting or discovering Docker state.</summary>
    [Fact]
    public async Task CliRoutesPlanningAndRejectsRedirectedOmission()
    {
        using var fixture = new UninstallCommandFixture();
        Assert.Equal(2, await fixture.Command("uninstall", "--plan"));
        Assert.Empty(fixture.Runner.Calls);
        Assert.Empty(fixture.Terminal.Prompts);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "uninstall-plans")));
        var plan = await fixture.Plan();
        Assert.Equal(UninstallBackup.Waived, plan.Backup);
        Assert.True(File.Exists(UninstallPreparation.PathFor(fixture.Root, plan.Hash())));
        Cli.ValidateCommand(["dispatch", "uninstall", "--accept-plan", plan.Hash()]);
    }

    /// <summary>Successful first backup configuration retains the exact committed installation UUID as protected identity residue.</summary>
    [Fact]
    public async Task PlanningAcceptsCommittedBackupIdentity()
    {
        using var fixture = new UninstallCommandFixture(true);
        var path = Path.Combine(fixture.Root, "backup-identity");
        var identity = fixture.Config.Installation.ToString("D");
        ProtectedFiles.Create(path, identity);
        var plan = await fixture.Plan(true);
        Assert.NotEqual(Guid.Empty, plan.Current.Installation);
        Assert.Equal(fixture.Config.Installation, plan.Current.Installation);
        Assert.True(plan.Current.Backup!.Enabled);
        ProtectedFiles.Check(path, 0);
        Assert.Equal(identity, File.ReadAllText(path));
        Assert.False(File.Exists(UninstallReceipt.PathFor(fixture.Root)));
    }

    /// <summary>Uncommitted, contradictory, malformed and unsafe retained identity files fail before any plan or receipt is published.</summary>
    [Theory]
    [InlineData("uncommitted")]
    [InlineData("mismatch")]
    [InlineData("empty")]
    [InlineData("malformed")]
    [InlineData("multiple")]
    [InlineData("oversized")]
    [InlineData("mode")]
    [InlineData("owner")]
    [InlineData("link")]
    public async Task PlanningRefusesInvalidBackupIdentity(string state)
    {
        using var fixture = new UninstallCommandFixture(state == "uncommitted" ? null : true);
        var path = Path.Combine(fixture.Root, "backup-identity");
        var identity = fixture.Config.Installation.ToString("D");
        var content = state switch
        {
            "uncommitted" or "mismatch" => Guid.NewGuid().ToString("D"),
            "empty" => Guid.Empty.ToString("D"),
            "malformed" => "invalid-uuid",
            "multiple" => identity + "\n" + identity,
            "oversized" => identity + new string(' ', 129),
            _ => identity
        };
        if (state == "link") File.CreateSymbolicLink(path, Path.Combine(fixture.Root, "installation.json"));
        else ProtectedFiles.Create(path, content, state == "owner" ? 1654u : 0u);
        if (state == "mode") File.SetUnixFileMode(path, ProtectedFiles.PrivateFile | UnixFileMode.GroupRead);
        Assert.Equal(2, await fixture.Command("uninstall", "--plan", "--without-backup"));
        Assert.Empty(fixture.Runner.Calls);
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "uninstall-plans")));
        Assert.False(File.Exists(UninstallReceipt.PathFor(fixture.Root)));
    }

    /// <summary>Enabled policy recommends backup, accepts an explicit no and cancels rather than guessing at EOF or invalid text.</summary>
    [Theory]
    [InlineData("", UninstallBackup.VerifiedQuiesced)]
    [InlineData("yes", UninstallBackup.VerifiedQuiesced)]
    [InlineData("N", UninstallBackup.Waived)]
    [InlineData("no", UninstallBackup.Waived)]
    [InlineData(null, null)]
    [InlineData("maybe", null)]
    public async Task InteractivePolicyChoice(string? answer, UninstallBackup? expected)
    {
        using var fixture = new UninstallCommandFixture(true);
        fixture.Terminal.Interactive = true;
        fixture.Terminal.Answers.Enqueue(answer);
        Assert.Equal(expected is not null ? 0 : answer is null ? 1 : 2, await fixture.Command("uninstall", "--plan"));
        Assert.Contains("[Y/n]", Assert.Single(fixture.Terminal.Prompts));
        if (expected is null) Assert.False(Directory.Exists(Path.Combine(fixture.Root, "uninstall-plans")));
        else Assert.Equal(expected, fixture.ReadPlan().Backup);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("run") || call[0] == "rm");
    }

    /// <summary>No policy or disabled policy requires the exact destructive waiver; Enter and a lowercase approximation do not authorize it.</summary>
    [Theory]
    [InlineData(null, "WITHOUT BACKUP", 0)]
    [InlineData(false, "WITHOUT BACKUP", 0)]
    [InlineData(null, "", 1)]
    [InlineData(false, "without backup", 2)]
    [InlineData(null, null, 1)]
    public async Task InteractiveUnavailableBackupRequiresExactWaiver(bool? enabled, string? answer, int result)
    {
        using var fixture = new UninstallCommandFixture(enabled);
        fixture.Terminal.Interactive = true;
        fixture.Terminal.Answers.Enqueue(answer);
        Assert.Equal(result, await fixture.Command("uninstall", "--purge", "--plan"));
        Assert.Contains("WITHOUT BACKUP", Assert.Single(fixture.Terminal.Prompts));
        Assert.Contains(fixture.Terminal.Output, line => line.Contains("destructive erasure"));
        if (result == 0) Assert.Equal(UninstallBackup.Waived, fixture.ReadPlan().Backup);
        Assert.False(File.Exists(UninstallReceipt.PathFor(fixture.Root)));
    }

    /// <summary>Even a backup-selected purge plan refuses before capture, receipt creation, fencing or Docker mutation.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PurgeAcceptanceIsNotEnabled(bool backup)
    {
        using var fixture = new UninstallCommandFixture(backup ? true : null);
        var plan = await fixture.Plan(backup, true);
        fixture.Runner.Calls.Clear();
        Assert.Equal(2, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Empty(fixture.Runner.Calls);
        Assert.False(File.Exists(UninstallReceipt.PathFor(fixture.Root)));
        Assert.Contains(fixture.Terminal.Errors, line => line.Contains("not enabled in Handoff 2"));
    }

    /// <summary>Final capture is quiesced and exactly verified; older-set retention does not change authorization of those committed bytes.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FinalBackupBindsCaptureVerificationAndCommittedBytes(bool retentionSucceeded)
    {
        using var fixture = new UninstallCommandFixture(true);
        fixture.Runner.RetentionSucceeded = retentionSucceeded;
        var plan = await fixture.Plan(true);
        var receiptExistedAtWorker = false;
        fixture.Runner.Before = args =>
        {
            if (args.Contains("run")) receiptExistedAtWorker |= File.Exists(UninstallReceipt.PathFor(fixture.Root));
        };
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        var receipt = UninstallReceipt.Load(fixture.Root)!;
        var capture = fixture.Runner.Captured!;
        Assert.Equal(retentionSucceeded, capture.RetentionSucceeded);
        Assert.Contains("Retention succeeded: " + retentionSucceeded, fixture.Terminal.Output);
        Assert.False(receiptExistedAtWorker);
        Assert.Equal(UninstallPhase.Preserved, receipt.Phase);
        Assert.Equal(capture.Archive, receipt.FinalBackup!.Archive);
        Assert.Equal(capture.Name, receipt.FinalBackup.Basename);
        Assert.Equal(capture.Completed, receipt.FinalBackup.Captured);
        Assert.True(receipt.FinalBackup.IntegrityValid && receipt.FinalBackup.CompatibilitySupported);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(fixture.Config.Backup!.Destination, capture.Name)))), receipt.FinalBackup.Sha256);
        Assert.Contains(fixture.Runner.Calls, call => call.Contains("stop") && call.TakeLast(4).SequenceEqual(new[] { "stop", "--timeout", "70", "wayfarer" }));
        var verify = Assert.Single(fixture.Runner.Calls, call => call.Contains("backup-reader") && call.Contains("run"));
        Assert.Equal(capture.Name, verify[^1]);
        var captureCount = fixture.Runner.Calls.Count(call => call.Contains("backup-worker"));
        fixture.Runner.Calls.Clear();
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Empty(fixture.Runner.Calls);
        Assert.Equal(1, captureCount);
    }

    /// <summary>Ordinary backup retains its nonzero retention status and visible result even though its new archive was published.</summary>
    [Fact]
    public async Task PublishedBackupWithFailedRetentionKeepsOrdinaryFailureStatus()
    {
        using var fixture = new UninstallCommandFixture(true);
        fixture.Runner.RetentionSucceeded = false;
        Assert.Equal(1, await fixture.Command("backup", "--quiesced"));
        Assert.False(fixture.Runner.Captured!.RetentionSucceeded);
        Assert.Contains("Retention succeeded: False", fixture.Terminal.Output);
        Assert.False(File.Exists(UninstallReceipt.PathFor(fixture.Root)));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("backup-reader"));
    }

    /// <summary>Capture failure, verifier mismatch, invalid integrity/compatibility and ambiguous publication never authorize removal.</summary>
    [Theory]
    [InlineData("backup")]
    [InlineData("verify")]
    [InlineData("integrity")]
    [InlineData("compatibility")]
    [InlineData("uuid")]
    [InlineData("name")]
    [InlineData("publication")]
    public async Task FailedFinalBackupHasNoDestructiveReceipt(string failure)
    {
        using var fixture = new UninstallCommandFixture(true);
        var plan = await fixture.Plan(true);
        fixture.Runner.Failure = failure is "backup" or "verify" ? failure : null;
        fixture.Runner.InvalidIntegrity = failure == "integrity";
        fixture.Runner.InvalidCompatibility = failure == "compatibility";
        fixture.Runner.WrongArchive = failure == "uuid";
        fixture.Runner.WrongName = failure == "name";
        fixture.Runner.ExtraArchive = failure == "publication";
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.False(File.Exists(UninstallReceipt.PathFor(fixture.Root)));
        Assert.NotEmpty(fixture.Runner.Containers);
        Assert.NotEmpty(fixture.Runner.Networks);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call[0] == "update" || call is ["network", "rm", ..]);
        Assert.Contains(fixture.Terminal.Errors, line => line.Contains("quiesced backup may have left the application stopped"));
    }

    /// <summary>Exact restart/stop/wait precede deletion, all absence evidence commits, and every volume/root authority survives.</summary>
    [Fact]
    public async Task NormalWaiverReachesPreservedWithoutDeletingAnyVolume()
    {
        using var fixture = new UninstallCommandFixture(true, true);
        var plan = await fixture.Plan();
        var configBytes = File.ReadAllBytes(Path.Combine(fixture.Root, "installation.json"));
        var phases = new List<UninstallPhase>();
        fixture.Runner.Before = args =>
        {
            if (UninstallReceipt.Load(fixture.Root) is { } receipt) phases.Add(receipt.Phase);
        };
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        var terminal = UninstallReceipt.Load(fixture.Root)!;
        Assert.Equal(UninstallPhase.Preserved, terminal.Phase);
        Assert.Null(terminal.FinalBackup);
        Assert.Empty(terminal.RemovedVolumes);
        Assert.Equal(configBytes, File.ReadAllBytes(Path.Combine(fixture.Root, "installation.json")));
        Assert.Empty(fixture.Runner.Containers);
        Assert.Empty(fixture.Runner.Networks);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("run") || call.Contains("down") || call.Contains("prune") || call is ["volume", "rm", ..]);
        foreach (var resource in plan.Resources.Where(r => r.Kind == UninstallResourceKind.Container && r.DockerId is not null))
        {
            var update = fixture.Runner.Calls.FindIndex(call => call.SequenceEqual(new[] { "update", "--restart=no", resource.DockerId! }));
            var stop = fixture.Runner.Calls.FindIndex(call => call[0] == "stop" && call[^1] == resource.DockerId);
            var wait = fixture.Runner.Calls.FindIndex(call => call.SequenceEqual(new[] { "wait", resource.DockerId! }));
            var remove = fixture.Runner.Calls.FindIndex(call => call.SequenceEqual(new[] { "rm", resource.DockerId! }));
            Assert.True(update >= 0 && update < stop && stop < wait && wait < remove);
            Assert.Contains(resource.DockerId!, terminal.RemovedContainers);
            Assert.Equal(resource.Role == "backup-scheduler" ? "30" : "70", fixture.Runner.Calls[stop][2]);
        }
        Assert.Equal(plan.Resources.Count(r => r.Kind == UninstallResourceKind.Network && r.DockerId is not null), terminal.RemovedNetworks.Length);
        Assert.Equal(phases.Order(), phases);
        Assert.All(plan.Resources.Where(r => r.Kind == UninstallResourceKind.Volume), resource =>
            Assert.True(Directory.Exists(Path.Combine(fixture.Runner.DockerRoot, "volumes", resource.Name, "_data"))));
        Assert.NotEqual(0, await fixture.Command("uninstall", "--accept-plan", new string('f', 64)));
    }

    /// <summary>A client error after actual container/network deletion still permits confirmed exact absence progress.</summary>
    [Theory]
    [InlineData("container")]
    [InlineData("network")]
    public async Task LostRemovalAcknowledgementIsReconciled(string kind)
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await fixture.Plan();
        fixture.Runner.LostRemoval = kind;
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(UninstallPhase.Preserved, UninstallReceipt.Load(fixture.Root)!.Phase);
    }

    /// <summary>A stopped but restart-enabled planned consumer or newly introduced foreign volume consumer cannot commit Fenced.</summary>
    [Theory]
    [InlineData("policy")]
    [InlineData("consumer")]
    [InlineData("replacement")]
    public async Task UnprovenFenceLeavesAuthorizedReceipt(string failure)
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await fixture.Plan();
        fixture.Runner.Failure = failure == "policy" ? "update" : null;
        fixture.Runner.Before = args =>
        {
            if (failure == "policy" || args[0] != "update") return;
            var facts = fixture.Runner.Containers["wayfarer"].Deserialize<Dictionary<string, JsonElement>>()!;
            facts["Id"] = JsonSerializer.SerializeToElement(new string('b', 64));
            if (failure == "replacement") fixture.Runner.Containers["wayfarer"] = JsonSerializer.SerializeToElement(facts);
            else
            {
                facts["Name"] = JsonSerializer.SerializeToElement("/foreign-consumer");
                var configuration = facts["Config"].Deserialize<Dictionary<string, JsonElement>>()!;
                var labels = configuration["Labels"].Deserialize<Dictionary<string, string>>()!;
                labels["com.docker.compose.project"] = "foreign";
                configuration["Labels"] = JsonSerializer.SerializeToElement(labels);
                facts["Config"] = JsonSerializer.SerializeToElement(configuration);
                fixture.Runner.Containers["foreign"] = JsonSerializer.SerializeToElement(facts);
            }
        };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(UninstallPhase.Authorized, UninstallReceipt.Load(fixture.Root)!.Phase);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call[0] == "rm" || call is ["network", "rm", ..]);
        Assert.Contains(fixture.Terminal.Errors, line => line.Contains(plan.Operation.ToString("D")) && line.Contains("Authorized") && line.Contains(plan.Hash()));
        Assert.DoesNotContain(fixture.Terminal.Errors, line => line.Contains("sensitive child"));
    }

    /// <summary>Per-container absence survives process interruption; replay uses only receipt authority even without the plan file.</summary>
    [Fact]
    public async Task InterruptedRemovalReplaysDurableProgressWithoutRecapture()
    {
        using var fixture = new UninstallCommandFixture(true);
        var plan = await fixture.Plan(true);
        var removals = 0;
        fixture.Runner.Before = args => { if (args[0] == "rm" && args[^1].Length == 64 && ++removals == 2) throw new OperationCanceledException(); };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        var interrupted = UninstallReceipt.Load(fixture.Root)!;
        Assert.Equal(UninstallPhase.Fenced, interrupted.Phase);
        Assert.Single(interrupted.RemovedContainers);
        Assert.Throws<UsageException>(() => interrupted.Advance(UninstallPhase.RuntimeRemoved).Validate(fixture.Root));
        File.Delete(UninstallPreparation.PathFor(fixture.Root, plan.Hash()));
        fixture.Runner.Before = null;
        fixture.Runner.Calls.Clear();
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        var complete = UninstallReceipt.Load(fixture.Root)!;
        Assert.Equal(UninstallPhase.Preserved, complete.Phase);
        Assert.Empty(interrupted.RemovedContainers.Except(complete.RemovedContainers));
        Assert.Empty(interrupted.RemovedNetworks.Except(complete.RemovedNetworks));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("run") || call[0] == "rm" && interrupted.RemovedContainers.Contains(call[^1]));
    }

    /// <summary>Forward replay refuses a new ID under an accepted network name and preserves all completed container evidence.</summary>
    [Fact]
    public async Task ReplacedNetworkCannotBecomeRemovalAuthority()
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await fixture.Plan();
        var stopped = false;
        fixture.Runner.Before = args =>
        {
            if (args is ["network", "rm", ..] && !stopped) { stopped = true; throw new OperationCanceledException(); }
        };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        var original = UninstallReceipt.Load(fixture.Root)!;
        Assert.Equal(UninstallPhase.Fenced, original.Phase);
        var name = fixture.Config.Project + "_backend";
        var replacement = fixture.Runner.Networks[name].Deserialize<Dictionary<string, JsonElement>>()!;
        replacement["Id"] = JsonSerializer.SerializeToElement(new string('b', 64));
        fixture.Runner.Networks[name] = JsonSerializer.SerializeToElement(replacement);
        fixture.Runner.Before = null;
        fixture.Runner.Calls.Clear();
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(original.RemovedContainers, UninstallReceipt.Load(fixture.Root)!.RemovedContainers);
        Assert.DoesNotContain(fixture.Runner.Calls, call => call[0] == "rm" || call is ["network", "rm", ..]);
        Assert.Equal(new string('b', 64), fixture.Runner.Networks[name].GetProperty("Id").GetString());
    }

    /// <summary>A capped process response cannot be mistaken for a complete inventory or evidence that accepted resources are absent.</summary>
    [Fact]
    public async Task TruncatedInventoryCannotAuthorizeRemoval()
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await fixture.Plan();
        fixture.Runner.TruncatedInventory = true;
        Assert.NotEqual(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.False(File.Exists(UninstallReceipt.PathFor(fixture.Root)));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call[0] == "update" || call[0] == "rm" || call is ["network", "rm", ..]);
    }
}
