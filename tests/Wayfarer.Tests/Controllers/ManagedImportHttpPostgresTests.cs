using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using Wayfarer.Middleware;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Real routed HTTP policy/auth/control evidence over guarded PostgreSQL and existing Identity host.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed partial class ManagedImportHttpPostgresTests(PostgresImportTestFixture fixture)
{
    private const string Route = "/api/mobile/location-imports";

    [PostgresFact]
    public async Task AcceptedReplayPrecedesDisableAndLowering_WithoutBodyReadOrAnotherImport()
    {
        await using var scope = await ScopeAsync();
        var content = "Latitude,Longitude,TimestampUtc\n" + new string(' ', 1024 * 1024);
        var settings = new ApplicationSettings { UploadSizeLimitMB = 0 };
        await using var app = await HostAsync(scope, settings);
        using var client = Client(app, scope.Credential);
        var request = Guid.NewGuid();
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        using (var response = await client.SendAsync(Put(request, bytes))) Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        foreach (var limit in new[] { -1, 1 })
        {
            settings.UploadSizeLimitMB = limit;
            using var replay = Put(request, bytes);
            replay.Headers.Add("X-Test-Reject-Read", "true");
            using var response = await client.SendAsync(replay);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.Equal("accepted", (await Json(response)).GetProperty("code").GetString());
        }
        using (var rejected = await client.SendAsync(Put(Guid.NewGuid(), bytes)))
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        settings.UploadSizeLimitMB = -1;
        using (var rejected = await client.SendAsync(Put(Guid.NewGuid(), "Latitude,Longitude,TimestampUtc\n"u8.ToArray())))
            Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.LocationImports.CountAsync(row => row.UserId == scope.User.Id));
    }

    [PostgresFact]
    public async Task OwnerIsolationRevocationAndWebAntiforgery_RemainIndependent()
    {
        await using var scope = await ScopeAsync();
        var row = await scope.AcceptAsync();
        var other = await fixture.CreateUserAsync();
        await using var app = await HostAsync(scope, new());
        using var client = Client(app, scope.Credential);
        using (var anonymous = app.GetTestClient())
        {
            anonymous.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, scope.User.Id));
            using var denied = await anonymous.GetAsync(Route + "/capabilities");
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
            Assert.True(denied.Headers.CacheControl!.NoStore);
        }
        await using (var db = fixture.CreateContext())
        {
            var token = await db.ApiTokens.SingleAsync(item => item.UserId == scope.User.Id);
            token.UserId = other.Id;
            await db.SaveChangesAsync();
        }
        using (var hidden = await client.GetAsync(Route + "/" + row.ImportId)) Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        using (var hidden = await client.GetAsync(Route + "/uploads/" + row.RequestId)) Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        await using (var db = fixture.CreateContext())
            await db.ApiTokens.Where(item => item.UserId == other.Id).ExecuteDeleteAsync();
        using (var denied = await client.GetAsync(Route + "/capabilities")) Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = null;
        client.DefaultRequestHeaders.Add("Cookie", IdentityRouteHost.Cookie(app.Services, scope.User.Id));
        using var web = await client.PostAsync("/User/LocationImport/StartImport?id=" + row.ImportId, new StringContent(""));
        Assert.Equal(HttpStatusCode.BadRequest, web.StatusCode);
        Assert.Equal(0, (await scope.ReadAsync(row.Id)).Import!.ExecutionEpoch);
    }

    [PostgresFact]
    public async Task RoutedCommandsRecoverLostRepliesAndCompletedPayloadRemovalRetainsResult()
    {
        await using var scope = await ScopeAsync();
        var row = await scope.AcceptAsync();
        await using var app = await HostAsync(scope, new());
        using var client = Client(app, scope.Credential);
        using (var started = await client.PostAsJsonAsync(Route + $"/{row.ImportId}/start", new { targetEpoch = 1 }))
            Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        await scope.DueAsync(row, ImportStatus.Completed);
        await scope.Cleanup.ProcessAsync(row.Id, default);
        using (var replay = await client.PostAsJsonAsync(Route + $"/{row.ImportId}/start", new { targetEpoch = 1 }))
        {
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            var import = (await Json(replay)).GetProperty("import");
            Assert.Equal("completed", import.GetProperty("state").GetString());
            Assert.Equal("removed", import.GetProperty("file").GetProperty("state").GetString());
        }
        using (var stopped = await client.PostAsJsonAsync(Route + $"/{row.ImportId}/stop", new { executionEpoch = 1 }))
            Assert.Equal(HttpStatusCode.OK, stopped.StatusCode);
        using (var deleted = await client.DeleteAsync(Route + "/" + row.ImportId)) Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        using var retired = await client.GetAsync(Route + "/uploads/" + row.RequestId);
        Assert.Equal(HttpStatusCode.Gone, retired.StatusCode);
        Assert.Equal("import_upload_retired", (await Json(retired)).GetProperty("code").GetString());
    }

    [PostgresFact]
    public async Task NewUploadsEnforceMetadataAndPersistExplicitEnrichmentChoice()
    {
        await using var scope = await ScopeAsync();
        await using var app = await HostAsync(scope, new());
        using var client = Client(app, scope.Credential);
        var bytes = "Latitude,Longitude,TimestampUtc\n"u8.ToArray();
        using (var request = Put(Guid.NewGuid(), bytes))
        {
            request.Content!.Headers.ContentType = new("application/zip");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        }
        using (var request = Put(Guid.NewGuid(), bytes))
        {
            request.Headers.Remove("X-File-Bytes");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }
        using (var request = Put(Guid.NewGuid(), bytes))
        {
            request.Headers.Remove("X-Enrichment-Requested");
            request.Headers.Add("X-Enrichment-Requested", "true");
            using var response = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.True((await Json(response)).GetProperty("import").GetProperty("enrichmentRequested").GetBoolean());
        }
        using var discovery = await client.GetAsync(Route + "/capabilities");
        var capability = await Json(discovery);
        Assert.True(capability.GetProperty("uploadsEnabled").GetBoolean());
        Assert.False(capability.TryGetProperty("ready", out _));
        Assert.False(capability.TryGetProperty("reason", out _));
        Assert.False(capability.TryGetProperty("managedIntakeConfigured", out _));
        await using var db = fixture.CreateContext();
        var accepted = await db.Set<MobileLocationImportUpload>().SingleAsync(row => row.UserId == scope.User.Id);
        Assert.True(accepted.EnrichmentRequested);
    }

    /// <summary>Discovery follows effective limits and the same store policy enforced by new intake.</summary>
    [PostgresFact]
    public async Task DiscoveryAgreesWithEffectiveUploadAndStorageRestrictions()
    {
        await using var scope = await ScopeAsync();
        var settings = new ApplicationSettings();
        await using var app = await HostAsync(scope, settings);
        using var client = Client(app, scope.Credential);
        var bytes = "Latitude,Longitude,TimestampUtc\n"u8.ToArray();
        try
        {
            foreach (var (configuredLimit, qualified, expectedLimit) in new[]
            {
                (0, true, 104857600L), (1, true, 1048576L), (-1, true, 0L), (1, false, 1048576L)
            })
            {
                settings.UploadSizeLimitMB = configuredLimit;
                scope.StorageOptions.LocalStoreQualified = qualified;
                var enabled = expectedLimit > 0 && qualified && OperatingSystem.IsLinux();
                using var discovery = await client.GetAsync(Route + "/capabilities");
                Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
                var capability = await Json(discovery);
                Assert.Equal(enabled, capability.GetProperty("uploadsEnabled").GetBoolean());
                Assert.Equal(expectedLimit, capability.GetProperty("maxFileBytes").GetInt64());
                Assert.Equal(expectedLimit, capability.GetProperty("maxRequestBytes").GetInt64());
                using var request = Put(Guid.NewGuid(), bytes);
                if (!enabled) request.Headers.Add("X-Test-Reject-Read", "true");
                using var intake = await client.SendAsync(request);
                Assert.Equal(expectedLimit == 0 ? HttpStatusCode.Forbidden
                    : enabled ? HttpStatusCode.Created : HttpStatusCode.ServiceUnavailable, intake.StatusCode);
            }
        }
        finally
        {
            // Restore qualified cleanup authority for this scope's accepted files.
            scope.StorageOptions.LocalStoreQualified = true;
        }
    }

    private async Task<WebApplication> HostAsync(ManagedImportTestScope scope, ApplicationSettings settings)
    {
        var db = fixture.CreateContext();
        return await IdentityRouteHost.StartAsync(db, Path.GetDirectoryName(scope.Files.DirectoryPath)!, services =>
        {
            services.AddScoped(_ => scope.Factory.CreateDbContext());
            services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(scope.Factory);
            services.AddSingleton(scope.Upload);
            services.AddSingleton(scope.Cleanup);
            services.AddSingleton(scope.Lifecycle);
            services.AddSingleton(new ManagedImportStatus(scope.Factory));
            services.AddSingleton(scope.Scheduler.Object);
            services.AddSingleton<Wayfarer.Services.LocationEnrichment.IWorkflowScheduleProjection>(enrichmentProjection.Object);
            services.AddSingleton<Wayfarer.Services.LocationProviders.IPersonalProviderStatusReader>(
                new Wayfarer.Services.LocationProviders.PersonalProviderStatusReader(scope.Factory,
                    CredentialTestFactory.Create(protection), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
            services.AddScoped<Wayfarer.Services.LocationEnrichment.ILocationEnrichmentProgressQuery,
                Wayfarer.Services.LocationEnrichment.LocationEnrichmentProgressQuery>();
            services.AddScoped<Wayfarer.Services.LocationEnrichment.IImportEnrichmentHandoff,
                Wayfarer.Services.LocationEnrichment.ImportEnrichmentHandoff>();
            services.AddScoped<Wayfarer.Services.LocationEnrichment.ILocationEnrichmentPresentationProjector,
                Wayfarer.Services.LocationEnrichment.LocationEnrichmentPresentationProjector>();
            services.AddSingleton(Options.Create(scope.StorageOptions));
            var policy = new Mock<IApplicationSettingsService>();
            policy.Setup(item => item.GetSettings()).Returns(settings);
            services.AddSingleton(policy.Object);
        }, configureAuthorized: host =>
        {
            host.Use(async (context, next) =>
            {
                context.Features.Set<IHttpMaxRequestBodySizeFeature>(new SizeFeature());
                if (context.Request.Headers.ContainsKey("X-Test-Reject-Read"))
                    context.Request.Body = new Wayfarer.Tests.Services.ManagedImportCustodyPostgresTests.ThrowingBody();
                await next(context);
            });
            host.UseMiddleware<ManagedImportMiddleware>();
            host.UseMiddleware<UserFileUploadMiddleware>();
            host.UseMiddleware<ApiErrorResponseMiddleware>();
        });
    }

    private static HttpClient Client(WebApplication app, string credential)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", credential);
        return client;
    }

    private static HttpRequestMessage Put(Guid id, byte[] bytes)
    {
        var fingerprint = ManagedImportTestScope.Fingerprint(bytes);
        var request = new HttpRequestMessage(HttpMethod.Put, Route + $"/uploads/{id}?format=csv") { Content = new ByteArrayContent(bytes) };
        request.Content.Headers.ContentType = new("text/csv");
        request.Headers.Add("X-File-Bytes", fingerprint.Bytes.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-File-SHA256", fingerprint.Sha256);
        request.Headers.Add("X-Enrichment-Requested", "false");
        return request;
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>());

    private async Task<ManagedImportTestScope> ScopeAsync()
    {
        var scope = new ManagedImportTestScope(fixture);
        await scope.InitializeAsync();
        return scope;
    }

    private sealed class SizeFeature : IHttpMaxRequestBodySizeFeature
    {
        public bool IsReadOnly => false;
        public long? MaxRequestBodySize { get; set; } = 104857600;
    }
}
