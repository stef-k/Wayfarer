using WayfarerCtl;
using WayfarerRecovery;
using System.Text.Json;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Only new update authorization and irreversible migration boundaries are tested here.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ManagedUpdateTests
{
    /// <summary>A schema success or single current generation cannot authorize target activation.</summary>
    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    public void HistoryAcceptanceRequiresBothTargetGenerations(bool time, bool text, bool ready)
    {
        var evidence = JsonSerializer.Serialize(new { Ready = true, Generations = new[] {
            new { Kind = "time", Ready = time }, new { Kind = "text", Ready = text } } });
        Assert.Equal(ready, UpdateHistoryPreparation.Ready(evidence));
    }

    /// <summary>Preparation resume and recovery both reject running, replaced or foreign target helpers.</summary>
    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void PreparationHelperRequiresStoppedExactTarget(bool running, bool foreign, bool accepted)
    {
        var config = new Deployment { Schema = 2, Installation = Guid.NewGuid(), Bundle = "/bundle",
            Hostname = "wayfarer.example.org", AppDigest = "sha256:" + new string('a', 64) };
        var plan = new UpdatePlan(Guid.NewGuid(), "/etc/wayfarer", config, config, null!, null!, [], "", "", "", null!);
        var receipt = new UpdateReceipt { Plan = plan, PlanHash = "test", Phase = UpdatePhase.MigrationStarted,
            HistoryContainer = config.Project + "-update-history-" + plan.Operation.ToString("N") };
        var helper = ManagedHistoryUpgradePostgresTests.Helper(receipt, true, running, foreign ? "foreign-image" : null);
        if (accepted) UpdateRuntime.VerifyMaintenanceHelper(plan.Root, receipt, helper, history: true);
        else Assert.Throws<IOException>(() => UpdateRuntime.VerifyMaintenanceHelper(plan.Root, receipt, helper, history: true));
    }

    /// <summary>Update has no implicit acceptance, network selection or lock/recovery waiver.</summary>
    [Fact]
    public void GrammarRequiresExactLocalAuthorization()
    {
        foreach (var args in new[] { new[] { "--yes" }, ["--bundle", "/trusted/release"],
                     ["--latest"], ["--without-backup"], ["--skip-lock"], ["--accept-plan", "not-a-hash"] })
            Assert.Throws<UsageException>(() => UpdateOptions.Parse(args));
        Assert.True(UpdateOptions.Parse(["--bundle", "/trusted/release", "--plan"]).Plan);
        var hash = new string('a', 64);
        Assert.Equal(hash, UpdateOptions.Parse(["--accept-plan", hash]).Accept);
        Assert.True(UpdateOptions.Parse(["--replace-release", "--bundle", "/trusted/release", "--plan"]).ReplaceRelease);
        Assert.Throws<UsageException>(() => UpdateOptions.Parse(["--replace-release", "--accept-plan", hash]));
        Assert.Throws<UsageException>(() => UpdateOptions.Parse(["--replace-release", "--replace-release", "--bundle", "/trusted/release", "--plan"]));
    }

    /// <summary>Lost migration acknowledgement is already beyond abort, regardless of target writer launch.</summary>
    [Fact]
    public void MigrationIntentIsTheAbortCutoff()
    {
        var receipt = new UpdateReceipt { Plan = null!, PlanHash = "test", Phase = UpdatePhase.RecoveryVerified };
        Assert.Equal(UpdatePhase.Aborted, receipt.Advance(UpdatePhase.Aborted).Phase);
        var started = receipt.Advance(UpdatePhase.MigrationStarted);
        Assert.True(started.MigrationPossible);
        Assert.False(started.WritesPossible);
        Assert.Throws<UsageException>(() => started.Advance(UpdatePhase.Aborted));
        Assert.Throws<UsageException>(() => started.Advance(UpdatePhase.Accepted));
        Assert.Equal(UpdatePhase.MigrationConfirmed, started.Advance(UpdatePhase.MigrationConfirmed).Phase);
    }

    /// <summary>Application-role maintenance cannot see writable durable files, ingress or backup/admin authority.</summary>
    [Fact]
    public void MigrationMaintenanceHasOnlyInternalDatabaseAuthority()
    {
        var config = new Deployment { Schema = 2, Installation = Guid.NewGuid(), Bundle = "/bundle",
            Hostname = "wayfarer.example.org", AppDigest = "sha256:" + new string('a', 64) };
        var args = UpdatePreparation.Maintenance("/etc/wayfarer", config);
        Assert.Contains("--user=1654:1654", args);
        Assert.Contains("--read-only", args);
        Assert.Contains("wayfarer_backend", args);
        Assert.Contains("wayfarer_app-data:/var/lib/wayfarer:ro", args);
        Assert.DoesNotContain(args, value => value.Contains("db-password") || value.Contains("docker.sock") ||
            value.Contains("destination") || value.Contains("_edge") || value.StartsWith("--publish"));
    }
}
