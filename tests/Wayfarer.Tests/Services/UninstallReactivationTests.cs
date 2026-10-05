using System.Text.Json;
using WayfarerCtl;
using WayfarerRecovery;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Preserved diagnosis, deliberate reactivation and ordinary lifecycle reuse are proven at the operator/process seam.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class UninstallReactivationTests
{
    /// <summary>Preserved status explains intentional removal; doctor proves retained authority without running-service diagnosis.</summary>
    [Fact]
    public async Task StatusDoctorAndStopRecognizeIntentionalUninstall()
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await Preserve(fixture);
        fixture.Runner.Calls.Clear();
        Assert.Equal(0, await fixture.Command("status"));
        Assert.Equal(0, await fixture.Command("stop"));
        Assert.Empty(fixture.Runner.Calls);
        Assert.Contains(fixture.Terminal.Output, line => line.Contains("intentionally uninstalled") && line.Contains("wayfarerctl start"));
        Assert.Equal(0, await fixture.Command("doctor"));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("up") || call.Contains("exec") || call.Contains("run"));
        Assert.Equal(plan.Hash(), UninstallReceipt.Load(fixture.Root)!.PlanHash);
    }

    /// <summary>Restart and runtime/data mutations refuse before any Docker operation, including backup configuration and user recovery.</summary>
    [Theory]
    [InlineData("restart")]
    [InlineData("update --plan")]
    [InlineData("restore --plan")]
    [InlineData("backup")]
    [InlineData("backup --quiesced")]
    [InlineData("backup configure --disable")]
    [InlineData("backups")]
    [InlineData("verify-backup")]
    [InlineData("user find admin")]
    [InlineData("user reset-password admin --password-stdin")]
    [InlineData("logs")]
    [InlineData("setup")]
    [InlineData("setup --resume")]
    public async Task PreservedMutationsCannotReactivate(string command)
    {
        using var fixture = new UninstallCommandFixture();
        await Preserve(fixture);
        fixture.Runner.Calls.Clear();
        Assert.NotEqual(0, await fixture.Command(command.Split(' ')));
        Assert.Empty(fixture.Runner.Calls);
        Assert.Contains(fixture.Terminal.Errors, line => line.Contains("wayfarerctl start"));
    }

    /// <summary>Malformed or changed protected authority cannot report preserved health or recreate services.</summary>
    [Theory]
    [InlineData("receipt", "status")]
    [InlineData("configuration", "status")]
    [InlineData("environment", "doctor")]
    [InlineData("secrets", "start")]
    [InlineData("operator", "doctor")]
    public async Task PreservedAuthorityChangesFailClosed(string changed, string command)
    {
        using var fixture = new UninstallCommandFixture();
        await Preserve(fixture);
        switch (changed)
        {
            case "receipt": File.WriteAllText(UninstallReceipt.PathFor(fixture.Root), "{}"); break;
            case "configuration": File.AppendAllText(Path.Combine(fixture.Root, "installation.json"), "\n"); break;
            case "environment": File.AppendAllText(Path.Combine(fixture.Root, "deployment.env"), "\n"); break;
            case "secrets": File.WriteAllText(Path.Combine(fixture.Root, "secrets/db-password"), new string('F', 64)); break;
            case "operator": File.WriteAllText(Path.Combine(fixture.Config.Bundle, "wayfarerctl"), "changed operator"); break;
        }
        fixture.Runner.Calls.Clear();
        Assert.NotEqual(0, await fixture.Command(command));
        Assert.Empty(fixture.Runner.Calls);
        Assert.DoesNotContain(fixture.Terminal.Output.TakeLast(1), line => line.Contains("Reactivation complete"));
    }

    /// <summary>Every originally present retained volume is mandatory before Compose; DB/app absence and replacement fail diagnosis and start.</summary>
    [Theory]
    [InlineData("db-data", false)]
    [InlineData("app-data", false)]
    [InlineData("app-data", true)]
    [InlineData("app-cache", false)]
    public async Task RetainedStorageIsProvenBeforeAnyComposeRecreation(string role, bool replace)
    {
        using var fixture = new UninstallCommandFixture();
        await Preserve(fixture);
        var name = ActiveStorage.Volume(fixture.Config, role);
        if (replace)
        {
            var directory = Path.Combine(fixture.Runner.DockerRoot, "volumes", name, "_data");
            Directory.Move(directory, directory + "-retained");
            Directory.CreateDirectory(directory);
        }
        else fixture.Runner.MissingVolumes.Add(name);
        fixture.Runner.Calls.Clear();
        Assert.Equal(1, await fixture.Command("doctor"));
        Assert.Equal(1, await fixture.Command("start"));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("up") || call.Contains("create") || call.Contains("run"));
        Assert.Equal(UninstallState.Preserved, UninstallReceipt.State(fixture.Root));
    }

    /// <summary>Absent-at-plan historical storage cannot silently reappear under an apparently valid generation label.</summary>
    [Fact]
    public async Task AbsentHistoricalVolumeCannotBeMaterialized()
    {
        using var fixture = new UninstallCommandFixture(true);
        var config = fixture.Config;
        var restorePlan = new RestorePlan(Guid.NewGuid(), fixture.Root, config, config.Installation, Guid.NewGuid(), new string('a', 64),
            DateTimeOffset.UnixEpoch, "quiesced", "bundle", "capture", "restore", null, Guid.NewGuid().ToString("N"), false, true, false)
            { OperatorOwner = config.Release };
        var candidate = RestoreCandidate.Configuration(restorePlan);
        var historical = ActiveStorage.Volume(candidate, "db-data");
        new RestoreReceipt { Plan = restorePlan, PlanHash = restorePlan.Hash(), Phase = RestorePhase.Aborted,
            Volumes = [historical] }.Save(fixture.Root);
        var plan = await Preserve(fixture);
        Assert.Null(Assert.Single(plan.Resources, resource => resource.Name == historical).DockerId);
        fixture.Runner.AdditionalVolumes.Add(historical, UninstallPlanningTests.Volume(candidate, "db-data"));
        Directory.CreateDirectory(Path.Combine(fixture.Runner.DockerRoot, "volumes", historical, "_data"));
        fixture.Runner.Calls.Clear();
        Assert.Equal(1, await fixture.Command("doctor"));
        Assert.Equal(1, await fixture.Command("start"));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("up") || call.Contains("create"));
    }

    /// <summary>Start uses only retained inputs and --pull never; ordinary live diagnosis precedes durable receipt archival and retirement.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HealthyStartReturnsToOrdinaryActiveLifecycle(bool? backupEnabled)
    {
        using var fixture = new UninstallCommandFixture(backupEnabled);
        var plan = await Preserve(fixture);
        var preservedBytes = File.ReadAllBytes(UninstallReceipt.PathFor(fixture.Root));
        fixture.EnableEndpoint();
        fixture.Runner.Calls.Clear();
        var intentDuringHealth = false;
        fixture.Runner.Before = args =>
        {
            if (args.Contains("healthcheck")) intentDuringHealth = UninstallReceipt.State(fixture.Root) == UninstallState.Preserved;
        };
        var started = await fixture.Command("start");
        Assert.True(started == 0, string.Join('\n', fixture.Terminal.Output.Concat(fixture.Terminal.Errors)));
        Assert.True(intentDuringHealth);
        Assert.Equal(UninstallState.None, UninstallReceipt.State(fixture.Root));
        Assert.Equal(preservedBytes, File.ReadAllBytes(UninstallReceipt.HistoryPath(fixture.Root, plan.Operation)));
        var up = fixture.Runner.Calls.Where(call => call.Contains("up")).ToArray();
        Assert.Equal(backupEnabled == true ? 2 : 1, up.Length);
        Assert.All(up, call => { Assert.Contains("never", call); Assert.Contains(fixture.Config.Bundle, call); Assert.DoesNotContain("migrate", call); });
        Assert.Equal(backupEnabled == true, up.Any(call => call.Contains("backup-scheduler")));
        var firstUp = fixture.Runner.Calls.FindIndex(call => call.Contains("up"));
        foreach (var role in new[] { "db-data", "app-data" })
            Assert.Contains(fixture.Runner.Calls.Take(firstUp), call => call.SequenceEqual(new[] { "volume", "inspect", ActiveStorage.Volume(fixture.Config, role) }));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call.Contains("pull") || call.Contains("migrate") || call.Contains("down"));
        Assert.NotEqual(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Contains(fixture.Terminal.Errors, line => line.Contains("retired by start"));
        Assert.Equal(0, await fixture.Command("stop"));
        Assert.Contains(fixture.Runner.Calls, call => call.SequenceEqual(fixture.Config.Compose(fixture.Root, "stop", "--timeout", "70")));
        Assert.Equal(UninstallStartingState.Active, (await fixture.Plan()).StartingState);
    }

    /// <summary>Failed health after canonical recreation leaves receipt current; new IDs independently validate on the next start.</summary>
    [Fact]
    public async Task PartialReactivationConvergesWithoutComparingRemovedRuntimeIds()
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await Preserve(fixture);
        fixture.EnableEndpoint();
        fixture.Runner.Before = args =>
        {
            if (args.Contains("healthcheck")) fixture.Runner.Failure = "compose";
        };
        Assert.Equal(1, await fixture.Command("start"));
        Assert.Equal(UninstallState.Preserved, UninstallReceipt.State(fixture.Root));
        Assert.NotEmpty(fixture.Runner.Containers);
        Assert.All(fixture.Runner.Containers.Values, container => Assert.DoesNotContain(plan.Resources,
            resource => resource.DockerId == container.GetProperty("Id").GetString()));
        fixture.Runner.Before = null;
        fixture.Runner.Failure = null;
        Assert.Equal(0, await fixture.Command("start"));
        Assert.Equal(UninstallState.None, UninstallReceipt.State(fixture.Root));
    }

    /// <summary>Healthy runtime cannot overwrite conflicting history or claim active completion; retry finishes the protected metadata commit.</summary>
    [Fact]
    public async Task FailedReceiptRetirementKeepsHealthyReactivationRetryable()
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await Preserve(fixture);
        fixture.EnableEndpoint();
        var path = UninstallReceipt.HistoryPath(fixture.Root, plan.Operation);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!, ProtectedFiles.PrivateDirectory);
        ProtectedFiles.Create(path, "conflicting history");
        Assert.Equal(1, await fixture.Command("start"));
        Assert.Equal(UninstallState.Preserved, UninstallReceipt.State(fixture.Root));
        Assert.Equal("conflicting history", File.ReadAllText(path));
        Assert.DoesNotContain(fixture.Terminal.Output, line => line.Contains("Reactivation complete"));
        File.WriteAllBytes(path, File.ReadAllBytes(UninstallReceipt.PathFor(fixture.Root)));
        fixture.Runner.Calls.Clear();
        Assert.Equal(0, await fixture.Command("start"));
        Assert.Equal(UninstallState.None, UninstallReceipt.State(fixture.Root));
        Assert.DoesNotContain(fixture.Runner.Calls, call => call[0] == "rm" || call is ["network", "rm", ..]);
    }

    /// <summary>Ordinary stop/restart share the extracted lifecycle owner without changing timeout, scheduler or health behavior.</summary>
    [Fact]
    public async Task OrdinaryRestartKeepsExistingLifecycleContract()
    {
        using var fixture = new UninstallCommandFixture(true);
        fixture.EnableEndpoint();
        Assert.Equal(0, await fixture.Command("restart"));
        Assert.Equal(UninstallState.None, UninstallReceipt.State(fixture.Root));
        var calls = fixture.Runner.Calls;
        var schedulerStop = calls.FindIndex(call => call.Contains("stop") && call[^1] == "backup-scheduler");
        var runtimeStop = calls.FindIndex(call => call.SequenceEqual(fixture.Config.Compose(fixture.Root, "stop", "--timeout", "70")));
        var firstUp = calls.FindIndex(call => call.Contains("up"));
        Assert.True(schedulerStop < runtimeStop && runtimeStop < firstUp);
        Assert.Contains(calls, call => call.Contains("healthcheck"));
    }

    /// <summary>Hosted executable authority is propagated to planning, receipt replay, diagnosis and reactivation without relaxing native checks.</summary>
    [Fact]
    public async Task HostedRetainedOperatorPathRemainsRequiredAndUsable()
    {
        using var fixture = new UninstallCommandFixture(hostedOperator: true);
        Assert.NotEqual(0, await new Cli(fixture.Runner, fixture.Terminal)
            .RunAsync(["--deployment-root", fixture.Root, "uninstall", "--plan", "--without-backup"]));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "uninstall-plans")));
        var plan = await Preserve(fixture);
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(0, await fixture.Command("doctor"));
        fixture.EnableEndpoint();
        Assert.Equal(0, await fixture.Command("start"));
        Assert.Equal(UninstallState.None, UninstallReceipt.State(fixture.Root));
    }

    /// <summary>Reach Preserved by the actual accepted normal command rather than constructing a falsely terminal receipt.</summary>
    private static async Task<UninstallPlan> Preserve(UninstallCommandFixture fixture)
    {
        var plan = await fixture.Plan();
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        return plan;
    }
}
