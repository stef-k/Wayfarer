using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Moq;
using Microsoft.Extensions.DependencyInjection;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Models;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Qualifies the delivered raw intake and conditional shared account adapter over routed HTTP.</summary>
public sealed partial class ManagedImportHttpPostgresTests
{
    private readonly EphemeralDataProtectionProvider protection = new();
    private readonly Mock<IWorkflowScheduleProjection> enrichmentProjection = new();

    /// <summary>Resolves actual Program registrations without starting hosted jobs or replacing coordination owners.</summary>
    [PostgresFact]
    public async Task ProductionRegistrationsShareProjectionAndImplicitHandoffOwners()
    {
        await using var scope = await ScopeAsync();
        var builder = Microsoft.AspNetCore.Builder.WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(builder.Configuration,
            new Dictionary<string, string?> { ["ConnectionStrings:DefaultConnection"] = "Host=unused;Database=unused",
                ["DataProtection:KeyRingPath"] = Path.Combine(scope.Files.DirectoryPath, "unused-keys") });
        var assembly = typeof(ApplicationDbContext).Assembly;
        foreach (var name in new[] { "ConfigureServices", "ConfigureQuartz" })
            assembly.GetType("Program")!.GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                .Single(method => method.Name.Contains("g__" + name + "|", StringComparison.Ordinal)).Invoke(null, [builder]);
        builder.Services.AddSingleton<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(protection);
        builder.Services.AddScoped(_ => scope.Factory.CreateDbContext());
        builder.Services.AddSingleton<Microsoft.EntityFrameworkCore.IDbContextFactory<ApplicationDbContext>>(scope.Factory);
        builder.Services.AddSingleton(scope.Files);
        builder.Services.AddSingleton(scope.Gate);
        builder.Services.AddSingleton(scope.Scheduler.Object);
        await using var provider = builder.Services.BuildServiceProvider();
        await using var resolved = provider.CreateAsyncScope();
        var services = resolved.ServiceProvider;
        var coordinator = services.GetRequiredService<Wayfarer.Services.LocationImports.LocationImportProjectionCoordinator>();
        Assert.Same(Wayfarer.Services.LocationImports.LocationImportProjectionCoordinator.Shared, coordinator);
        foreach (var owner in new object[] {
            services.GetRequiredService<Wayfarer.Services.LocationImports.ILocationImportLifecycle>(),
            services.GetRequiredService<Wayfarer.Services.LocationImports.LocationImportReconciler>() })
            Assert.Same(coordinator, owner.GetType().GetField("projectionCoordinator",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(owner));
        var handoff = services.GetRequiredService<IImportEnrichmentHandoff>();
        Assert.IsType<ImportEnrichmentHandoff>(handoff);
        var importer = services.GetRequiredService<Wayfarer.Parsers.ILocationImportService>();
        Assert.Same(handoff, importer.GetType().GetField("_enrichmentHandoff",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(importer));
        var continuation = services.GetRequiredService<Wayfarer.Services.LocationImports.LocationImportEnrichmentHandoff>();
        Assert.Same(handoff, continuation.GetType().GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .Single(field => field.FieldType == typeof(IImportEnrichmentHandoff)).GetValue(continuation));
        Assert.IsType<Wayfarer.Services.LocationProviders.GeoapifyLocationBackfillService>(services.GetRequiredService<ILocationEnrichmentBatch>());
        Assert.IsType<LocationEnrichmentPresentationProjector>(services.GetRequiredService<ILocationEnrichmentPresentationProjector>());
    }

    [PostgresTheory]
    [InlineData("csv", true)]
    [InlineData("csv", false)]
    [InlineData("wayfarer-geojson", true)]
    [InlineData("wayfarer-geojson", false)]
    public async Task RawIntakePreservesChoiceWithoutStartingOrStoppingAccountWork(string format, bool choice)
    {
        await using var scope = await ScopeAsync();
        await SeedEnrichmentAsync(scope);
        await using var app = await HostAsync(scope, new());
        using var client = Client(app, scope.Credential);
        using (var start = await client.PostAsJsonAsync(Route + "/enrichment/start", new { expectedEpoch = 0, expectedState = "Idle" }))
            Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var bytes = System.Text.Encoding.UTF8.GetBytes(format == "csv"
            ? "Latitude,Longitude,TimestampUtc\n1,2,2026-10-01T00:00:00Z\n"
            : "{\"type\":\"FeatureCollection\",\"features\":[]}");
        var id = Guid.NewGuid();
        using var request = Put(id, bytes);
        request.RequestUri = new(Route + $"/uploads/{id}?format={format}", UriKind.Relative);
        request.Content!.Headers.ContentType = new(format == "csv" ? "text/csv" : "application/geo+json");
        request.Headers.Remove("X-Enrichment-Requested");
        request.Headers.Add("X-Enrichment-Requested", choice ? "true" : "false");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var imported = (await Json(response)).GetProperty("import");
        Assert.Equal(choice, imported.GetProperty("enrichmentRequested").GetBoolean());
        Assert.False(imported.GetProperty("enrichmentHandoffPending").GetBoolean());
        Assert.Equal("stopped", imported.GetProperty("state").GetString());
        await using var verify = fixture.CreateContext();
        var row = await verify.Set<MobileLocationImportUpload>().Include(x => x.Import).SingleAsync(x => x.UserId == scope.User.Id);
        Assert.Equal(choice, row.EnrichmentRequested);
        Assert.Equal(choice, row.Import!.EnrichmentRequestedAtUtc.HasValue);
        var workflow = await verify.LocationEnrichmentWorkflows.SingleAsync(x => x.UserId == scope.User.Id);
        Assert.Equal(LocationEnrichmentState.Scheduled, workflow.State);
        Assert.Equal(1, workflow.Epoch);
    }

    [PostgresFact]
    public async Task StaleControlsConflictAndUserHoldIsSeparateFromBudgetAndImportProgress()
    {
        await using var scope = await ScopeAsync();
        await SeedEnrichmentAsync(scope);
        await using var app = await HostAsync(scope, new());
        using var client = Client(app, scope.Credential);
        using (var invalid = await client.PostAsJsonAsync(Route + "/enrichment/start", new { expectedEpoch = 0 }))
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var started = await client.PostAsJsonAsync(Route + "/enrichment/start", new { expectedEpoch = 0, expectedState = "Idle" }))
            Assert.Equal("Applied", (await Json(started)).GetProperty("classification").GetString());
        using (var stale = await client.PostAsJsonAsync(Route + "/enrichment/cancel", new { expectedEpoch = 0, expectedState = "Idle" }))
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        using (var paused = await client.PostAsJsonAsync(Route + "/enrichment/pause", new { expectedEpoch = 1, expectedState = "Scheduled" }))
            Assert.Equal(HttpStatusCode.OK, paused.StatusCode);
        await using (var setup = fixture.CreateContext())
        {
            setup.Add(new GeoapifyUsageGuard { UserId = scope.User.Id, CreditLimit = 1 });
            setup.Add(new GeoapifyUsageAdmission { UserId = scope.User.Id, Credits = 1,
                Product = PersonalProviderProduct.Geocoding, AdmittedAt = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }
        using (var stale = await client.PostAsJsonAsync(Route + "/enrichment/resume", new { expectedEpoch = 1, expectedState = "PausedByUser" }))
            Assert.Equal("stale-control", (await Json(stale)).GetProperty("code").GetString());
        using (var exhausted = await client.PostAsJsonAsync(Route + "/enrichment/resume", new { expectedEpoch = 2, expectedState = "PausedByUser" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, exhausted.StatusCode);
            Assert.Equal("budget-exhausted", (await Json(exhausted)).GetProperty("code").GetString());
        }
        using var status = await client.GetAsync(Route + "/enrichment");
        var state = await Json(status);
        Assert.True(status.Headers.CacheControl!.NoStore);
        Assert.Equal("PausedByUser", state.GetProperty("statusText").GetString());
        Assert.Equal(2, state.GetProperty("epoch").GetInt32());
        Assert.True(state.GetProperty("budgetExhausted").GetBoolean());
        Assert.True(state.GetProperty("credentialAuthorityAvailable").GetBoolean());
        Assert.Equal("Admitted", state.GetProperty("credentialAuthorityCategory").GetString());
        Assert.False(state.GetProperty("intentEnabled").GetBoolean());
        Assert.True(state.GetProperty("totalOutstanding").GetInt32() > 0);
        Assert.DoesNotContain(scope.Credential, state.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-provider-secret", state.ToString(), StringComparison.Ordinal);
        using var cancelled = await client.PostAsJsonAsync(Route + "/enrichment/cancel", new { expectedEpoch = 2, expectedState = "PausedByUser" });
        Assert.Equal(HttpStatusCode.OK, cancelled.StatusCode);
    }

    [PostgresFact]
    public async Task SchedulingPendingReportsSavedIntentAndOldStartCannotReplayAfterPause()
    {
        await using var scope = await ScopeAsync();
        await SeedEnrichmentAsync(scope);
        enrichmentProjection.Setup(x => x.ProjectAsync(scope.User.Id, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("fixture scheduler unavailable"));
        await using var app = await HostAsync(scope, new());
        using var client = Client(app, scope.Credential);
        using (var pending = await client.PostAsJsonAsync(Route + "/enrichment/start", new { expectedEpoch = 0, expectedState = "Idle" }))
        {
            Assert.Equal(HttpStatusCode.Accepted, pending.StatusCode);
            var result = await Json(pending);
            Assert.True(result.GetProperty("durableIntentSaved").GetBoolean());
            Assert.True(result.GetProperty("schedulingPending").GetBoolean());
            Assert.Equal("read_status_then_new_intent", result.GetProperty("recovery").GetString());
        }
        using (var paused = await client.PostAsJsonAsync(Route + "/enrichment/pause", new { expectedEpoch = 1, expectedState = "Scheduled" }))
            Assert.Equal(HttpStatusCode.Accepted, paused.StatusCode);
        using var oldStart = await client.PostAsJsonAsync(Route + "/enrichment/start", new { expectedEpoch = 0, expectedState = "Idle" });
        Assert.Equal(HttpStatusCode.Conflict, oldStart.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Equal(LocationEnrichmentState.PausedByUser,
            (await db.LocationEnrichmentWorkflows.SingleAsync(x => x.UserId == scope.User.Id)).State);
    }

    private async Task SeedEnrichmentAsync(ManagedImportTestScope scope)
    {
        await using var db = fixture.CreateContext();
        var profile = PersonalLocationProviderProfile.Create(scope.User.Id, PersonalLocationProvider.Geoapify);
        var credentials = CredentialTestFactory.Create(protection);
        credentials.Replace(profile, "fixture-provider-secret");
        profile.GeocodingAuthorized = true;
        credentials.RecordVerification(profile, PersonalProviderCapability.Geocoding, PersonalProviderVerification.Verified);
        db.AddRange(profile, new PersonalLocationProviderSelection { UserId = scope.User.Id, GeocodingProviderKey = "geoapify" },
            new Location { UserId = scope.User.Id, Timestamp = DateTime.UtcNow, LocalTimestamp = DateTime.UtcNow,
                TimeZoneId = "UTC", Coordinates = new Point(23, 37) { SRID = 4326 } });
        await db.SaveChangesAsync();
    }
}
