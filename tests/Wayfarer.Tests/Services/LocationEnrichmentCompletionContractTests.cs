using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Locks the remaining resumable-enrichment behavior before production completion.</summary>
public sealed class LocationEnrichmentCompletionContractTests
{
    private static readonly DateTime Now = new(2026, 8, 23, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PermanentPoisonAttemptIsSkippedWhileLaterCandidateRemainsEligible()
    {
        var poison = Attempt(LocationEnrichmentOutcome.InvalidCoordinates, 1, null);
        var later = new LocationEnrichmentAttempt { UserId = "user", LocationId = 2 };

        Assert.False(poison.IsEligible("geoapify", 3, 4, 5, Now));
        Assert.True(later.IsEligible("geoapify", 3, 4, 5, Now));
    }

    [Theory]
    [InlineData(LocationEnrichmentOutcome.RetryableFailure, 1, true)]
    [InlineData(LocationEnrichmentOutcome.RetryableFailure, 3, false)]
    [InlineData(LocationEnrichmentOutcome.InvalidCoordinates, 1, false)]
    [InlineData(LocationEnrichmentOutcome.NoResult, 1, false)]
    public void AttemptOutcomePersistsBoundedRetryAuthority(
        LocationEnrichmentOutcome outcome, int admitted, bool expected)
    {
        var attempt = Attempt(outcome, admitted, Now.AddMinutes(-1));

        Assert.Equal(expected, attempt.IsEligible("geoapify", 3, 4, 5, Now));
    }

    [Fact]
    public void ProviderGenerationChangeDoesNotImplicitlyRetryPermanentAttempt()
    {
        var attempt = Attempt(LocationEnrichmentOutcome.NoResult, 1, null);

        Assert.False(attempt.IsEligible("mapbox", 9, 9, 9, Now));
        attempt.ResetDeferred("mapbox", 9, 9, 9, Now);
        Assert.True(attempt.IsEligible("mapbox", 9, 9, 9, Now));
    }

    [Fact]
    public void GeoapifyExhaustionWithoutCountedAdmissionRequiresGuardReevaluation()
        => Assert.Null(LocationEnrichmentRetryPolicy.TryGeoapifyWake(
            new DateTimeOffset(Now), [new DateTimeOffset(Now.AddHours(-24))]));

    [Fact]
    public void GeoapifyBudgetWakeUsesExactStrictCutoffAndSafetyMargin()
    {
        var now = new DateTimeOffset(Now);
        var wake = LocationEnrichmentRetryPolicy.TryGeoapifyWake(now,
            [now.AddHours(-24), now.AddHours(-23), now.AddMinutes(-1)]);

        Assert.Equal(now.AddHours(1).AddSeconds(5), wake);
    }

    [Fact]
    public void RetryDeferredIsExplicitAndIdempotent()
    {
        var workflow = LocationEnrichmentWorkflow.Create("user", Now);
        workflow.Start(Now);
        workflow.PauseForAuthority(LocationEnrichmentOutcome.AuthorityUnavailable, Now.AddSeconds(1));

        Assert.True(workflow.RetryDeferred(Now.AddSeconds(2)));
        var epoch = workflow.Epoch;
        Assert.False(workflow.RetryDeferred(Now.AddSeconds(3)));
        Assert.Equal(epoch, workflow.Epoch);
    }

    [Fact]
    public void InvalidCommandTransitionsReturnBoundedConflicts()
    {
        var workflow = LocationEnrichmentWorkflow.Create("user", Now);

        Assert.False(workflow.TryPause(Now, out var idleReason));
        Assert.Equal("invalid-state", idleReason);
        workflow.Start(Now);
        workflow.Cancel(Now.AddSeconds(1));
        Assert.False(workflow.TryPause(Now.AddSeconds(2), out var cancelledReason));
        Assert.Equal("invalid-state", cancelledReason);
        Assert.False(workflow.TryResume(Now.AddSeconds(3), authorityAvailable: true, out _));
    }

    [Fact]
    public void CorrectedAuthorityCanResumeAuthorityPauseButNotBudgetPause()
    {
        var workflow = LocationEnrichmentWorkflow.Create("user", Now);
        workflow.Start(Now);
        workflow.PauseForAuthority(LocationEnrichmentOutcome.AuthorityUnavailable, Now.AddSeconds(1));

        Assert.True(workflow.TryResume(Now.AddSeconds(2), authorityAvailable: true, out _));
        workflow.ContinueAs(LocationEnrichmentState.PausedByBudget,
            LocationEnrichmentOutcome.BudgetExhausted, Now.AddHours(1), Now.AddSeconds(3));
        Assert.False(workflow.TryResume(Now.AddSeconds(4), authorityAvailable: true, out var reason));
        Assert.Equal("invalid-state", reason);
    }

    private static LocationEnrichmentAttempt Attempt(
        LocationEnrichmentOutcome outcome, int admitted, DateTime? next)
        => new()
        {
            UserId = "user", LocationId = 1, ProviderKey = "geoapify",
            CredentialGeneration = 3, ConfigurationGeneration = 4,
            SelectionGeneration = 5, Outcome = outcome, AdmittedAttemptCount = admitted,
            LastAttemptAtUtc = Now.AddMinutes(-5), NextAttemptAtUtc = next
        };
}
