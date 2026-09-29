using WayfarerCtl;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Only new update authorization and irreversible migration boundaries are tested here.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("linux")]
public sealed class ManagedUpdateTests
{
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
