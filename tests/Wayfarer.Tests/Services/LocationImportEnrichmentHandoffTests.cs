using Wayfarer.Parsers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines which provider outcomes short-circuit only enrichment for an import execution.</summary>
public sealed class LocationImportEnrichmentHandoffTests
{
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

        await new ImportEnrichmentHandoff(db, projection.Object).EnsureAsync("user");

        var workflow = await db.LocationEnrichmentWorkflows.SingleAsync();
        Assert.Equal(LocationEnrichmentState.PausedByAuthority, workflow.State);
        Assert.True(workflow.IntentEnabled);
        projection.Verify(item => item.ProjectAsync("user", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData(ReverseGeocodingCategory.Exhausted)]
    [InlineData(ReverseGeocodingCategory.NoProviderSelected)]
    [InlineData(ReverseGeocodingCategory.CredentialRequired)]
    [InlineData(ReverseGeocodingCategory.Unauthorized)]
    [InlineData(ReverseGeocodingCategory.VerificationRequired)]
    [InlineData(ReverseGeocodingCategory.StaleAuthority)]
    public void RunWideNoContactOutcomesDisableRemainingInlineAttempts(ReverseGeocodingCategory category)
        => Assert.True(LocationImportService.IsRunWideNoContact(category));

    [Theory]
    [InlineData(ReverseGeocodingCategory.InvalidRequest)]
    [InlineData(ReverseGeocodingCategory.InvalidResponse)]
    public void RecordSpecificOutcomesDoNotDisableRemainingInlineAttempts(ReverseGeocodingCategory category)
        => Assert.False(LocationImportService.IsRunWideNoContact(category));
}
