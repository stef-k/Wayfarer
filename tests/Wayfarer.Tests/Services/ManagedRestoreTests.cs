using System.Text.Json;
using WayfarerCtl;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Restore storage and irreversible writer boundaries at their deterministic product owners.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ManagedRestoreTests
{
    /// <summary>Persistent helper names cannot authorize a replacement foreign container or another durable generation.</summary>
    [Theory]
    [InlineData("other", "wayfarer_app-data", false)]
    [InlineData("wayfarer", "foreign_app-data", false)]
    [InlineData("wayfarer", "wayfarer_app-data", true)]
    public void ReconciliationRequiresActualOwnedMounts(string project, string volume, bool accepted)
    {
        var target = Config();
        var plan = new RestorePlan(Guid.NewGuid(), "/etc/wayfarer", target, Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), DateTimeOffset.UnixEpoch, "quiesced", "bundle", "capture", "restore", null,
            Guid.NewGuid().ToString("N"), false, false, true);
        var receipt = new RestoreReceipt { Plan = plan, PlanHash = plan.Hash() };
        var container = JsonSerializer.SerializeToElement(new
        {
            Name = "/wayfarer-wayfarer-1",
            Config = new { Image = "ghcr.io/stef-k/wayfarer@" + target.AppDigest,
                Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = project } },
            Mounts = new[] { new { Type = "volume", Name = volume, Destination = "/var/lib/wayfarer" } }
        });
        if (accepted) RestoreFencing.VerifyOwned(receipt, container);
        else Assert.Throws<IOException>(() => RestoreFencing.VerifyOwned(receipt, container));
    }

    /// <summary>The PG18 parent volume is durable authority even when PGDATA is a nested upstream directory.</summary>
    [Theory]
    [InlineData("wayfarer_db-data", true)]
    [InlineData("foreign_db-data", false)]
    public void ReconciliationFencesPostgreSql18VolumeRoot(string volume, bool accepted)
    {
        var target = Config();
        var plan = new RestorePlan(Guid.NewGuid(), "/etc/wayfarer", target, Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), DateTimeOffset.UnixEpoch, "quiesced", "bundle", "capture", "restore", null,
            Guid.NewGuid().ToString("N"), false, false, true);
        var receipt = new RestoreReceipt { Plan = plan, PlanHash = plan.Hash() };
        var container = JsonSerializer.SerializeToElement(new
        {
            Name = "/wayfarer-db-1",
            Config = new { Image = "ghcr.io/stef-k/wayfarer-db@" + target.DbDigest,
                Labels = new Dictionary<string, string> { ["com.docker.compose.project"] = "wayfarer", ["com.docker.compose.service"] = "db" } },
            Mounts = new[] { new { Type = "volume", Name = volume, Destination = "/var/lib/postgresql" } }
        });
        if (accepted) RestoreFencing.VerifyOwned(receipt, container);
        else Assert.Throws<IOException>(() => RestoreFencing.VerifyOwned(receipt, container));
    }

    /// <summary>Destructive authorization has no permissive alias or ambiguous external/owned selector.</summary>
    [Theory]
    [InlineData("--yes")]
    [InlineData("../backup.tar")]
    [InlineData("--archive relative.tar")]
    [InlineData("--new-install")]
    [InlineData("--accept-plan nope")]
    [InlineData("--resume 00000000-0000-0000-0000-000000000000")]
    [InlineData("--abort 00000000-0000-0000-0000-000000000001 --plan")]
    [InlineData("--plan --plan")]
    public void RestoreRejectsAmbiguousOrImplicitAuthorization(string input) =>
        Assert.Throws<UsageException>(() => RestoreOptions.Parse(input.Split(' ')));

    /// <summary>Retry generation changes never change the originally authorized archive or canonical plan hash.</summary>
    [Fact]
    public void FailedStagingRetryUsesFreshPairedVolumes()
    {
        var plan = new RestorePlan(Guid.NewGuid(), "/etc/wayfarer", Config(), Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), DateTimeOffset.UnixEpoch, "quiesced", "bundle", "capture", "restore", null,
            Guid.NewGuid().ToString("N"), false, false, true);
        var receipt = new RestoreReceipt { Plan = plan, PlanHash = plan.Hash(), Phase = RestorePhase.Staging };
        var retry = receipt with { CandidateAttempt = 1 };
        Assert.NotEqual(receipt.EffectivePlan.CandidateGeneration, retry.EffectivePlan.CandidateGeneration);
        Assert.Equal(receipt.PlanHash, retry.PlanHash);
        Assert.Equal(plan.ArchiveSha256, retry.EffectivePlan.ArchiveSha256);
        foreach (var role in new[] { "db-data", "app-data", "app-cache" })
            Assert.NotEqual(ActiveStorage.Volume(RestoreCandidate.Configuration(receipt.EffectivePlan), role),
                ActiveStorage.Volume(RestoreCandidate.Configuration(retry.EffectivePlan), role));
    }

    /// <summary>A failed actual restart update cannot persist Accepted or fabricate completion evidence.</summary>
    [Fact]
    public async Task RestartRestorationFailureLeavesFinalCheckpointUncommitted()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Wayfarer.Tests.Infrastructure.TestDirectory();
        Directory.CreateDirectory(Path.Combine(fixture.Path, "recovery-control"));
        var plan = new RestorePlan(Guid.NewGuid(), fixture.Path, Config(), Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), DateTimeOffset.UnixEpoch, "quiesced", "bundle", "capture", "restore", null,
            Guid.NewGuid().ToString("N"), true, true, true);
        var receipt = new RestoreReceipt { Plan = plan, PlanHash = plan.Hash(), Phase = RestorePhase.WritesPossible };
        var runner = new FailingRestartRunner();
        await Assert.ThrowsAsync<IOException>(() => new RestoreActivation(runner).FinishAsync(fixture.Path, receipt, CancellationToken.None));
        Assert.True(runner.UpdateAttempted);
        Assert.False(File.Exists(RestoreReceipt.PathFor(fixture.Path)));
        Assert.False(File.Exists(Path.Combine(fixture.Path, "restore-complete")));
        Assert.False(InstallationCompletion.IsComplete(fixture.Path));
        Assert.Throws<UsageException>(() => receipt.Advance(RestorePhase.Aborted));
    }

    /// <summary>Returns a real-looking canonical ID, then refuses the first policy update.</summary>
    private sealed class FailingRestartRunner : IProcessRunner
    {
        public bool UpdateAttempted { get; private set; }
        public Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
        {
            if (args.Contains("ps")) return Task.FromResult(new ProcessResult(0, "canonical-db"));
            Assert.Equal(new[] { "update", "--restart=unless-stopped", "canonical-db" }, args);
            UpdateAttempted = true;
            return Task.FromResult(new ProcessResult(1, "policy update failed"));
        }
    }

    /// <summary>Capacity includes file bytes, DB/index/WAL allowance, cache and a separate operating reserve.</summary>
    [Fact]
    public void CapacityRefusesObviousShortageWithoutTreatingDumpAsDatabaseSize()
    {
        var required = RestoreCapacity.CandidateBytes(10, 20, 3 * RestoreCapacity.Reserve);
        Assert.Equal(7 * RestoreCapacity.Reserve + 20, required);
        Assert.Throws<IOException>(() => RestoreCapacity.Check(required, required));
        RestoreCapacity.Check(required + RestoreCapacity.Reserve, required);
        Assert.Throws<OverflowException>(() => RestoreCapacity.CandidateBytes(long.MaxValue, 0, 0));
    }

    /// <summary>Superseded verification data is reclaimed without following a link into retained evidence.</summary>
    [Fact]
    public void VerificationCleanupRefusesLinkedEvidence()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var fixture = new Wayfarer.Tests.Infrastructure.TestDirectory();
        var staging = Path.Combine(fixture.Path, "verified");
        Directory.CreateDirectory(Path.Combine(staging, "keys"));
        File.WriteAllText(Path.Combine(staging, "keys", "key.xml"), "old");
        using var owned = new WayfarerRecovery.SafeDirectory(staging);
        owned.Clear();
        Assert.Empty(Directory.EnumerateFileSystemEntries(staging));
        var evidence = Path.Combine(fixture.Path, "frozen");
        File.WriteAllText(evidence, "retained");
        File.CreateSymbolicLink(Path.Combine(staging, "database.dump"), evidence);
        Assert.Throws<IOException>(() => owned.Clear());
        Assert.Equal("retained", File.ReadAllText(evidence));
        File.Delete(Path.Combine(staging, "database.dump"));
    }

    private static Deployment Config() => new()
    {
        Schema = 2, Installation = Guid.NewGuid(), Bundle = "/opt/wayfarer/bundle",
        Hostname = "wayfarer.example.org", AppDigest = "sha256:" + new string('a', 64)
    };

    /// <summary>All durable roles change together while local logs and proxy state keep canonical authority.</summary>
    [Fact]
    public void GenerationRoutesEveryDurableRoleThroughOneComposeOverlay()
    {
        var previous = Config();
        var next = previous with { Schema = 3, StorageGeneration = Guid.NewGuid().ToString("N") };
        next.Validate();
        using var overlay = JsonDocument.Parse(ActiveStorage.Render(next));
        foreach (var role in new[] { "db-data", "app-data", "app-cache" })
        {
            Assert.Equal("wayfarer_" + role, ActiveStorage.Volume(previous, role));
            var volume = overlay.RootElement.GetProperty("volumes").GetProperty(role);
            Assert.Equal(ActiveStorage.Volume(next, role), volume.GetProperty("name").GetString());
            Assert.True(volume.GetProperty("external").GetBoolean());
        }
        Assert.Equal("wayfarer_app-logs", ActiveStorage.Volume(next, "app-logs"));
        Assert.Equal("wayfarer_caddy-data", ActiveStorage.Volume(next, "caddy-data"));
        Assert.Contains(ActiveStorage.OverlayPath("/etc/wayfarer", next), next.Compose("/etc/wayfarer", "up"));
        Assert.Throws<UsageException>(() => (next with { Schema = 2 }).Validate());
        Assert.Throws<UsageException>(() => (next with { StorageGeneration = "../foreign" }).Validate());
    }

    /// <summary>Failed writer launch is still past rollback cutoff; phase skips cannot fabricate validation.</summary>
    [Fact]
    public void WriterCutoffIsIrreversible()
    {
        var plan = new RestorePlan(Guid.NewGuid(), "/etc/wayfarer", Config(), Guid.NewGuid(), Guid.NewGuid(),
            new string('a', 64), DateTimeOffset.UnixEpoch, "quiesced", "bundle", "capture", "restore", null,
            Guid.NewGuid().ToString("N"), false, false, true);
        var receipt = new RestoreReceipt { Plan = plan, PlanHash = plan.Hash() };
        receipt.Validate(plan.Root);
        Assert.Throws<UsageException>(() => receipt.Advance(RestorePhase.Accepted));
        Assert.Equal(RestorePhase.Aborted, receipt.Advance(RestorePhase.Aborted).Phase);
        foreach (var phase in new[] { RestorePhase.Fenced, RestorePhase.EmergencyVerifiedOrWaived,
            RestorePhase.Staging, RestorePhase.CandidateValidated, RestorePhase.ActivationIntent,
            RestorePhase.ActivatedStopped, RestorePhase.WritesPossible }) receipt = receipt.Advance(phase);
        Assert.True(receipt.WritesPossible);
        Assert.Throws<UsageException>(() => receipt.Advance(RestorePhase.Aborted));
        Assert.NotEqual(plan.Hash(), (plan with { WithoutEmergencyBackup = true }).Hash());
        Assert.NotEqual(plan.Hash(), (plan with { CandidateGeneration = Guid.NewGuid().ToString("N") }).Hash());
    }
}
