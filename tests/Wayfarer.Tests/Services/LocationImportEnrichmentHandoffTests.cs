using Wayfarer.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves committed import enrichment handoff and explicit Start eligibility.</summary>
public sealed class LocationImportEnrichmentHandoffTests
{
    [Theory]
    [InlineData(LocationEnrichmentOutcome.RetryableFailure, true, false)]
    [InlineData(LocationEnrichmentOutcome.NoResult, false, false)]
    [InlineData(LocationEnrichmentOutcome.InvalidCoordinates, false, false)]
    [InlineData(LocationEnrichmentOutcome.None, false, true)]
    public async Task CraftedStartRejectsWhenNoCurrentAuthorityCandidateIsRunnable(
        LocationEnrichmentOutcome outcome, bool futureDue, bool enriched)
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options, services);
        var user = TestDataFixtures.CreateUser(id: "user");
        var location = TestDataFixtures.CreateLocation(user);
        if (enriched) location.Address = "manual address";
        db.AddRange(user, location);
        await db.SaveChangesAsync();
        var now = DateTime.UtcNow;
        var binding = new PersonalProviderAuthorityBinding("geoapify", Guid.NewGuid(), 1, 1, 1,
            PersonalProviderVerification.Verified, 1, 1, null, null, null);
        if (!enriched)
        {
            db.LocationEnrichmentAttempts.Add(new LocationEnrichmentAttempt
            {
                UserId = user.Id, LocationId = location.Id, ProviderKey = binding.ProviderKey,
                ProviderProfileId = binding.ProfileId, Capability = PersonalProviderCapability.Geocoding,
                CredentialGeneration = 1, ConfigurationGeneration = 1, SelectionGeneration = 1,
                Verification = PersonalProviderVerification.Verified,
                VerificationCredentialGeneration = 1, VerificationGeneration = 1,
                Outcome = outcome, AdmittedAttemptCount = 1, LastAttemptAtUtc = now,
                NextAttemptAtUtc = futureDue ? now.AddHours(1) : null
            });
            await db.SaveChangesAsync();
        }
        var status = new Mock<IPersonalProviderStatusReader>();
        status.Setup(item => item.InspectPersistentGeocodingAsync(user.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalProviderInspection(PersonalProviderAdmissionCategory.Admitted,
                "geoapify", true, false, null, new(0, 2500, "credits", null, null), binding, now));
        var projection = new Mock<IWorkflowScheduleProjection>();
        var handoff = new ImportEnrichmentHandoff(
            db, projection.Object, status.Object, new LocationEnrichmentProgressQuery(db));

        var result = await handoff.StartAsync(user.Id);

        Assert.Equal(LocationEnrichmentCommandResult.Conflict, result.Classification);
        Assert.Equal("no-candidates", result.Code);
        Assert.Empty(db.LocationEnrichmentWorkflows);
        projection.Verify(item => item.ProjectAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task OptInWithoutProviderRetainsPausedWorkflowForLaterResume()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new ApplicationDbContext(options, services);
        db.Users.Add(new ApplicationUser { Id = "user", UserName = "user", DisplayName = "User" });
        await db.SaveChangesAsync();
        var projection = new Mock<IWorkflowScheduleProjection>();

        var inspection = new Mock<IPersonalProviderStatusReader>();
        inspection.Setup(item => item.InspectPersistentGeocodingAsync("user", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalProviderInspection(PersonalProviderAdmissionCategory.NoProviderSelected,
                null, false, false, null, null, null));

        var progress = new Mock<ILocationEnrichmentProgressQuery>();
        await new ImportEnrichmentHandoff(db, projection.Object, inspection.Object, progress.Object).EnsureAsync("user");

        var workflow = await db.LocationEnrichmentWorkflows.SingleAsync();
        Assert.Equal(LocationEnrichmentState.PausedByAuthority, workflow.State);
        Assert.True(workflow.IntentEnabled);
        projection.Verify(item => item.ProjectAsync("user", It.IsAny<CancellationToken>()), Times.Once);
    }

}
