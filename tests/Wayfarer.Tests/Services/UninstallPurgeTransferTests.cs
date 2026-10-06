using WayfarerCtl;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Preserved-to-purge is one explicit atomic ownership transfer; normal unresolved receipts cannot be superseded.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
[Trait("Category", "RequiresRoot")]
public sealed class UninstallPurgeTransferTests
{
    /// <summary>Once atomic transfer commits, the new Authorized receipt alone owns replay even if its original plan file is removed.</summary>
    [Fact]
    public async Task TransferredAuthorizedReceiptReplaysWithoutPlanFile()
    {
        using var fixture = new UninstallCommandFixture();
        var normal = await fixture.Plan();
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", normal.Hash()));
        var previous = File.ReadAllBytes(UninstallReceipt.PathFor(fixture.Root));
        var purge = await fixture.Plan(purge: true);
        fixture.Runner.Before = c =>
        {
            if (c[0] == "info" && UninstallReceipt.Load(fixture.Root) is { Phase: UninstallPhase.Authorized } receipt &&
                receipt.PlanHash == purge.Hash()) throw new OperationCanceledException();
        };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", purge.Hash()));
        Assert.Equal(UninstallPhase.Authorized, UninstallReceipt.Load(fixture.Root)!.Phase);
        Assert.Equal(purge.Hash(), UninstallReceipt.Load(fixture.Root)!.PlanHash);
        Assert.Equal(previous, File.ReadAllBytes(UninstallReceipt.HistoryPath(fixture.Root, normal.Operation)));
        File.Delete(UninstallPreparation.PathFor(fixture.Root, purge.Hash()));
        fixture.Runner.Before = null;
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", purge.Hash()));
    }

    /// <summary>Interrupted archive/preparation leaves the old exact receipt current; replay accepts matching history and converges without runtime resurrection.</summary>
    [Theory]
    [InlineData("archived")]
    [InlineData("prepared")]
    public async Task InterruptedTransferRetainsAnOwnerAndReplays(string point)
    {
        using var fixture = new UninstallCommandFixture(true);
        var normal = await fixture.Plan();
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", normal.Hash()));
        var previous = File.ReadAllBytes(UninstallReceipt.PathFor(fixture.Root));
        var purge = await fixture.Plan(purge: true);
        fixture.Runner.Calls.Clear();
        fixture.PurgeCheckpoint = name =>
        {
            if (name != point) return;
            Assert.Equal(previous, File.ReadAllBytes(UninstallReceipt.PathFor(fixture.Root)));
            throw new IOException("interrupted metadata handoff");
        };
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", purge.Hash()));
        Assert.Equal(previous, File.ReadAllBytes(UninstallReceipt.PathFor(fixture.Root)));
        Assert.Equal(previous, File.ReadAllBytes(UninstallReceipt.HistoryPath(fixture.Root, normal.Operation)));
        Assert.Equal(UninstallState.Preserved, UninstallReceipt.State(fixture.Root));
        Assert.DoesNotContain(fixture.Runner.Calls, c => c is ["volume", "rm", ..]);
        var sawAuthorized = false;
        fixture.PurgeCheckpoint = name =>
        {
            if (name == "secrets") Assert.True(sawAuthorized);
        };
        fixture.Runner.Before = c =>
        {
            if (UninstallReceipt.Load(fixture.Root) is { Phase: UninstallPhase.Authorized } receipt)
            {
                Assert.Equal(purge.Hash(), receipt.PlanHash);
                Assert.Equal(UninstallStartingState.Preserved, receipt.Plan.StartingState);
                Assert.Null(receipt.FinalBackup);
                sawAuthorized = true;
            }
        };
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", purge.Hash()));
        Assert.True(sawAuthorized);
        Assert.Equal(UninstallState.Purged, UninstallReceipt.State(fixture.Root));
        Assert.DoesNotContain(fixture.Runner.Calls, c => c[0] is "update" or "stop" or "rm" || c.Contains("up") || c.Contains("run"));
    }

    /// <summary>Conflicting history or transfer bytes remain untouched and never authorize a purge receipt or volume mutation.</summary>
    [Theory]
    [InlineData("history")]
    [InlineData("temporary")]
    public async Task ConflictingTransferEvidenceRefuses(string conflict)
    {
        using var fixture = new UninstallCommandFixture();
        var normal = await fixture.Plan();
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", normal.Hash()));
        var previous = File.ReadAllBytes(UninstallReceipt.PathFor(fixture.Root));
        var purge = await fixture.Plan(purge: true);
        var path = conflict == "history" ? UninstallReceipt.HistoryPath(fixture.Root, normal.Operation) :
            Path.Combine(fixture.Root, UninstallReceipt.TransferName(purge));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!, ProtectedFiles.PrivateDirectory);
        ProtectedFiles.Create(path, "conflicting bytes");
        fixture.Runner.Calls.Clear();
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", purge.Hash()));
        Assert.Equal(previous, File.ReadAllBytes(UninstallReceipt.PathFor(fixture.Root)));
        Assert.Equal("conflicting bytes", File.ReadAllText(path));
        Assert.DoesNotContain(fixture.Runner.Calls, c => c is ["volume", "rm", ..]);
    }

    /// <summary>Preserved purge cannot select backup or adopt newly appeared canonical resources before ownership transfer.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PreservedPurgeRejectsBackupAndRuntimeResurrection(bool runtime)
    {
        using var fixture = new UninstallCommandFixture(true);
        var container = fixture.Runner.Containers["db"];
        var normal = await fixture.Plan();
        Assert.Equal(0, await fixture.Command("uninstall", "--accept-plan", normal.Hash()));
        Assert.Equal(2, await fixture.Command("uninstall", "--purge", "--plan", "--backup"));
        var purge = await fixture.Plan(purge: true);
        if (runtime) fixture.Runner.Containers.Add("db", container);
        else File.AppendAllText(Path.Combine(fixture.Root, "deployment.env"), "\n");
        fixture.Runner.Calls.Clear();
        Assert.Equal(1, await fixture.Command("uninstall", "--accept-plan", purge.Hash()));
        Assert.Equal(normal.Hash(), UninstallReceipt.Load(fixture.Root)!.PlanHash);
        Assert.DoesNotContain(fixture.Runner.Calls, c => c[0] == "rm" || c is ["volume", "rm", ..]);
    }
}
