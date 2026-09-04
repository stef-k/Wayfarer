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

/// <summary>Proves page projection reloads relational progress and provider-owned usage.</summary>
public sealed class LocationEnrichmentPresentationProjectorTests
{
    [Fact]
    public async Task FreshContextProjectsFutureManualAndInvalidRowsExactly()
    {
        var database = Guid.NewGuid().ToString();
        using var services = new ServiceCollection().AddEntityFrameworkInMemoryDatabase().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(database).UseInternalServiceProvider(services).Options;
        var now = DateTime.UtcNow;
        var binding = Binding();
        await using (var seed = new ApplicationDbContext(options, services))
        {
            var user = TestDataFixtures.CreateUser(id: "projection-user");
            var future = TestDataFixtures.CreateLocation(user);
            var manual = TestDataFixtures.CreateLocation(user);
            var invalid = TestDataFixtures.CreateLocation(user);
            seed.AddRange(user, future, manual, invalid);
            await seed.SaveChangesAsync();
            seed.AddRange(Attempt(future.Id, binding, LocationEnrichmentOutcome.RetryableFailure, now.AddHours(2)),
                Attempt(manual.Id, binding, LocationEnrichmentOutcome.NoResult, null),
                Attempt(invalid.Id, binding, LocationEnrichmentOutcome.InvalidCoordinates, null));
            var workflow = LocationEnrichmentWorkflow.Create(user.Id, now);
            workflow.Start(now);
            workflow.ContinueAs(LocationEnrichmentState.BackingOff,
                LocationEnrichmentOutcome.RetryableFailure, now.AddHours(2), now);
            seed.Add(workflow);
            await seed.SaveChangesAsync();
        }

        await using var reload = new ApplicationDbContext(options, services);
        var projector = new LocationEnrichmentPresentationProjector(
            reload, Inspector(binding, used: 7, now), new LocationEnrichmentProgressQuery(reload));
        var view = await projector.ProjectAsync("projection-user");

        Assert.Equal(0, view.RunnableRemaining);
        Assert.Equal(1, view.FutureDue);
        Assert.Equal(1, view.ManualRetryAvailable);
        Assert.Equal(1, view.CannotBeRetried);
        Assert.True(view.DeferredWorkRetryable);
        Assert.Equal(7, view.ProviderUsage);
        Assert.NotNull(view.NextAttemptAtUtc);
        Assert.InRange(view.NextAttemptAtUtc.Value, now.AddHours(2).AddSeconds(-1), now.AddHours(2).AddSeconds(1));
        Assert.Equal("Waiting for a bounded retry.", view.PausedReason);
    }

    [Fact]
    public async Task ProviderLedgerUsageDoesNotUseWorkflowAdmissionCounter()
    {
        await using var db = CreateContext();
        var user = TestDataFixtures.CreateUser(id: "usage-user");
        var workflow = LocationEnrichmentWorkflow.Create(user.Id, DateTime.UtcNow);
        workflow.RecordBatch(1, 1, 0, 0, 99, DateTime.UtcNow);
        db.AddRange(user, workflow);
        await db.SaveChangesAsync();

        var view = await new LocationEnrichmentPresentationProjector(db,
                Inspector(Binding(), used: 4, DateTime.UtcNow), new LocationEnrichmentProgressQuery(db))
            .ProjectAsync(user.Id);

        Assert.Equal(4, view.ProviderUsage);
    }

    private static ApplicationDbContext CreateContext()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        return new(options, services);
    }

    private static IPersonalProviderStatusReader Inspector(
        PersonalProviderAuthorityBinding binding, int used, DateTime now)
    {
        var mock = new Mock<IPersonalProviderStatusReader>();
        mock.Setup(item => item.InspectPersistentGeocodingAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PersonalProviderInspection(PersonalProviderAdmissionCategory.Admitted, "geoapify",
                true, false, null, new(used, 2500, "credits", new DateTimeOffset(now.AddHours(-24), TimeSpan.Zero), null), binding, now));
        return mock.Object;
    }

    private static PersonalProviderAuthorityBinding Binding() => new("geoapify", Guid.Parse("82df97e6-f7b8-44fc-8bfd-2aa07842cd2e"),
        2, 3, 4, PersonalProviderVerification.Verified, 2, 3, null, null, null);

    private static LocationEnrichmentAttempt Attempt(int locationId, PersonalProviderAuthorityBinding binding,
        LocationEnrichmentOutcome outcome, DateTime? next) => new()
    {
        UserId = "projection-user", LocationId = locationId, ProviderKey = binding.ProviderKey,
        ProviderProfileId = binding.ProfileId, Capability = PersonalProviderCapability.Geocoding,
        CredentialGeneration = binding.CredentialGeneration, ConfigurationGeneration = binding.CapabilityGeneration,
        SelectionGeneration = binding.SelectionGeneration, Verification = binding.Verification,
        VerificationCredentialGeneration = binding.VerifiedCredentialGeneration,
        VerificationGeneration = binding.VerifiedCapabilityGeneration, Outcome = outcome,
        AdmittedAttemptCount = 1, LastAttemptAtUtc = DateTime.UtcNow, NextAttemptAtUtc = next
    };
}
