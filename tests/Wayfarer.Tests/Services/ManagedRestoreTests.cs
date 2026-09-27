using System.Text.Json;
using WayfarerCtl;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Restore storage and irreversible writer boundaries at their deterministic product owners.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ManagedRestoreTests
{
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
