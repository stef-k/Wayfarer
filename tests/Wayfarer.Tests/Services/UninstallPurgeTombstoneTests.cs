using System.Text.Json;
using WayfarerCtl;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Minimal terminal evidence owns replay, diagnosis and dispatch only after exact protected root cleanup.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class UninstallPurgeTombstoneTests
{
    /// <summary>Purged remains a semantic transition for tombstone derivation, never permission to persist the old full plan as terminal storage.</summary>
    [Fact]
    public async Task FullPurgedReceiptCannotBeStoredOrLoaded()
    {
        using var fixture = new UninstallCommandFixture();
        await UninstallPurgeRootTests.ReachVolumesRemoved(fixture);
        var terminal = UninstallReceipt.Load(fixture.Root)!.Advance(UninstallPhase.Purged);
        Assert.Throws<UsageException>(() => terminal.Save(fixture.Root));
        Assert.Equal(UninstallPhase.VolumesRemoved, UninstallReceipt.Load(fixture.Root)!.Phase);
        File.WriteAllText(UninstallReceipt.PathFor(fixture.Root), JsonSerializer.Serialize(terminal));
        Assert.Throws<UsageException>(() => UninstallReceipt.Load(fixture.Root));
        Assert.False(File.Exists(Path.Combine(fixture.Root, UninstallPurgeTombstone.Name)));
    }

    /// <summary>Terminal storage has exactly five non-secret fields; same-hash replay and diagnosis need no deleted authority or Docker.</summary>
    [Fact]
    public async Task TerminalReplayAndDiagnosisUseOnlyMinimalEvidence()
    {
        using var fixture = new UninstallCommandFixture(true);
        var plan = await Purge(fixture);
        var path = Path.Combine(fixture.Root, UninstallPurgeTombstone.Name);
        var bytes = File.ReadAllBytes(path);
        using var json = JsonDocument.Parse(bytes);
        Assert.Equal(new[] { "Operation", "OperatorOwner", "PlanHash", "Result", "Schema" }, json.RootElement.EnumerateObject().Select(p => p.Name).Order());
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        foreach (var forbidden in new[] { plan.Current.Installation.ToString("D"), plan.Current.Project, plan.Current.Hostname,
            plan.Current.Backup!.Destination, plan.SecretsFingerprint, "Current", "Configuration", "Resources", "FinalBackup" })
            Assert.DoesNotContain(forbidden, text);
        Assert.False(File.Exists(UninstallPreparation.PathFor(fixture.Root, plan.Hash())));
        Assert.False(File.Exists(UninstallReceipt.PathFor(fixture.Root)));
        fixture.Runner.Calls.Clear();
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(0, await fixture.Command("status"));
        Assert.Equal(0, await fixture.Command("doctor"));
        Assert.Equal(2, await fixture.Command("uninstall", "--accept-plan", new string('f', 64)));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Empty(fixture.Runner.Calls);
        Assert.Contains(fixture.Terminal.Output, line => line.Contains("Purged") && line.Contains("fresh wayfarerctl setup"));
        Assert.Contains(fixture.Terminal.Output, line => line.Contains("old installation"));
        Assert.Equal(plan.OperatorOwner, ReleaseAuthority.From(ReleaseDispatch.Select(fixture.Root, false)));
    }

    /// <summary>All ordinary active mutations and nonterminal uninstall/setup-resume forms fail before Docker access.</summary>
    [Theory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("restart")]
    [InlineData("logs")]
    [InlineData("update --plan")]
    [InlineData("restore --plan")]
    [InlineData("backup")]
    [InlineData("backup configure --disable")]
    [InlineData("backups")]
    [InlineData("verify-backup")]
    [InlineData("user find admin")]
    [InlineData("user reset-password admin --password-stdin")]
    [InlineData("setup --resume")]
    [InlineData("uninstall --purge --plan --without-backup")]
    [InlineData("release protocol")]
    public async Task PurgedCommandsRefuse(string command)
    {
        using var fixture = new UninstallCommandFixture();
        await Purge(fixture);
        fixture.Runner.Calls.Clear();
        Assert.NotEqual(0, await fixture.Command(command.Split(' ')));
        Assert.Empty(fixture.Runner.Calls);
        Assert.Equal(UninstallState.Purged, UninstallReceipt.State(fixture.Root));
        Assert.Equal(0, await fixture.Command("help"));
        Assert.Equal(0, await fixture.Command("version"));
    }

    /// <summary>Malformed minimal authority, unsafe files, missing/changed retained bytes and contradictory residue cannot report Purged health.</summary>
    [Theory]
    [InlineData("schema")]
    [InlineData("operation")]
    [InlineData("hash")]
    [InlineData("result")]
    [InlineData("owner")]
    [InlineData("owner-hash")]
    [InlineData("operator")]
    [InlineData("extra-field")]
    [InlineData("duplicate-field")]
    [InlineData("residue")]
    [InlineData("mode")]
    public async Task InvalidTerminalAuthorityFailsClosed(string change)
    {
        using var fixture = new UninstallCommandFixture();
        await Purge(fixture);
        var tombstone = UninstallPurgeTombstone.Load(fixture.Root)!;
        var path = Path.Combine(fixture.Root, UninstallPurgeTombstone.Name);
        var altered = change switch
        {
            "schema" => tombstone with { Schema = 2 },
            "operation" => tombstone with { Operation = Guid.Empty },
            "hash" => tombstone with { PlanHash = "invalid" },
            "result" => tombstone with { Result = "preserved" },
            "owner" => tombstone with { OperatorOwner = tombstone.OperatorOwner with { Name = "../foreign" } },
            "owner-hash" => tombstone with { OperatorOwner = tombstone.OperatorOwner with { OperatorSha256 = new string('f', 64) } },
            _ => tombstone
        };
        File.WriteAllText(path, JsonSerializer.Serialize(altered));
        if (change == "operator") File.WriteAllText(Path.Combine(fixture.Config.Bundle, "wayfarerctl"), "changed retained bytes");
        if (change == "extra-field") File.WriteAllText(path, JsonSerializer.Serialize(tombstone).Insert(1, "\"Installation\":\"old\","));
        if (change == "duplicate-field") File.WriteAllText(path, JsonSerializer.Serialize(tombstone).Insert(1, "\"Schema\":1,"));
        if (change == "residue") ProtectedFiles.Create(Path.Combine(fixture.Root, "installation.json"), "contradictory");
        if (change == "mode") File.SetUnixFileMode(path, ProtectedFiles.PrivateFile | UnixFileMode.GroupRead);
        fixture.Runner.Calls.Clear();
        Assert.NotEqual(0, await fixture.Command("status"));
        Assert.NotEqual(0, await fixture.Command("doctor"));
        Assert.NotEqual(0, await fixture.Command("setup"));
        Assert.ThrowsAny<Exception>(() => ReleaseDispatch.Select(fixture.Root, false));
        Assert.Empty(fixture.Runner.Calls);
    }

    /// <summary>Durable matching tombstone plus full receipt is an unresolved final transition; exact replay removes its predecessor with no Docker/config access.</summary>
    [Fact]
    public async Task MatchingFinalTransitionConverges()
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await fixture.Plan(purge: true);
        fixture.PurgeCheckpoint = name => { if (name == UninstallPurgeTombstone.Name) throw new IOException("after tombstone flush"); };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(UninstallState.Unresolved, UninstallReceipt.State(fixture.Root));
        Assert.Equal(UninstallPhase.VolumesRemoved, UninstallReceipt.Load(fixture.Root)!.Phase);
        Assert.NotNull(UninstallPurgeTombstone.Load(fixture.Root));
        fixture.Runner.Calls.Clear();
        Assert.Equal(1, await fixture.Command("status"));
        Assert.NotEqual(0, await fixture.Command("setup"));
        fixture.PurgeCheckpoint = null;
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.Equal(UninstallState.Purged, UninstallReceipt.State(fixture.Root));
        Assert.Empty(fixture.Runner.Calls);
    }

    /// <summary>A premature or mismatching pair preserves both files, never deletes a contradictory tombstone and never claims terminal state.</summary>
    [Theory]
    [InlineData("operation")]
    [InlineData("phase")]
    [InlineData("residue")]
    public async Task ContradictoryFinalPairRefuses(string conflict)
    {
        using var fixture = new UninstallCommandFixture();
        var plan = await UninstallPurgeRootTests.ReachVolumesRemoved(fixture);
        var receipt = UninstallReceipt.Load(fixture.Root)!;
        var tombstone = UninstallPurgeTombstone.From(fixture.Root, receipt);
        if (conflict == "operation") tombstone = tombstone with { Operation = Guid.NewGuid() };
        if (conflict == "phase") File.WriteAllText(UninstallReceipt.PathFor(fixture.Root), JsonSerializer.Serialize(receipt with { Phase = UninstallPhase.RuntimeRemoved }));
        ProtectedFiles.Create(Path.Combine(fixture.Root, UninstallPurgeTombstone.Name), JsonSerializer.Serialize(tombstone));
        fixture.PurgeCheckpoint = null;
        fixture.Runner.Calls.Clear();
        Assert.NotEqual(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        Assert.True(File.Exists(UninstallReceipt.PathFor(fixture.Root)));
        Assert.True(File.Exists(Path.Combine(fixture.Root, UninstallPurgeTombstone.Name)));
        Assert.ThrowsAny<Exception>(() => UninstallReceipt.State(fixture.Root));
        Assert.Empty(fixture.Runner.Calls);
    }

    /// <summary>Complete purge through the public accepted-plan command rather than constructing terminal authority in tests.</summary>
    internal static async Task<UninstallPlan> Purge(UninstallCommandFixture fixture)
    {
        var plan = await fixture.Plan(purge: true);
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", plan.Hash()));
        return plan;
    }
}
