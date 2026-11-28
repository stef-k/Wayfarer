using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Wayfarer.Services;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Wayfarer.Services.TripMetadataReceipts;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>The actual self-service POST waits before the account lock while a claimed Place create initializes its meter.</summary>
    [PostgresFact]
    public async Task StructureReceipt_FirstUseAdmissionOrdersWithSelfServiceDeletion()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        var region = await SeedStructureRegionAsync(db, trip);
        const string password = "Disposable protected provider setup!";
        var user = await db.Users.SingleAsync(row => row.Id == trip.UserId);
        user.PasswordHash = new PasswordHasher<ApplicationUser>().HashPassword(user, password);
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        await SeedProtectedProviderAsync(db, user.Id, credentials);
        var contact = new CountProviderContact();
        var probe = new FirstUseDeletionProbe();
        await using var app = await StartProtectedProviderHostAsync(db, credentials, contact, probe);
        using var web = await WebAsync(app, user.Id);
        using var deletion = await WebAsync(app, user.Id);
        var (route, body, permit) = await IssuePlaceCreateAsync(web, trip, region);
        var pending = TripMetadataReceiptHttp.SendAsync(web, HttpMethod.Post, route, permit, body);
        try
        {
            if (await Task.WhenAny(probe.InsertReached.Task, pending).WaitAsync(TimeSpan.FromSeconds(8)) == pending)
            {
                using var early = await pending;
                Assert.Fail($"Admission finished before meter insertion ({(int)early.StatusCode}): {await early.Content.ReadAsStringAsync()}");
            }
            var holder = await probe.InsertReached.Task.WaitAsync(TimeSpan.FromSeconds(8));
            using var duplicate = await TripMetadataReceiptHttp.SendAsync(web, HttpMethod.Post, route, permit, body);
            await TripMetadataReceiptHttp.EvidenceAsync(duplicate, permit, "unobserved", 409);

            using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["Input.Password"] = password });
            var erase = deletion.PostAsync("/Identity/Account/Manage/DeletePersonalData", form);
            var waiter = await probe.DeletionFenceRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.NotEqual(holder, waiter);
            using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1)))
                await ObserveProviderBlockAsync(holder, waiter, deadline.Token);
            Assert.False(probe.DeletionAccountLocked);
            probe.ReleaseInsert.TrySetResult();
            await probe.BeforeContact.Task.WaitAsync(TimeSpan.FromSeconds(8));
            using var removed = await erase;
            Assert.Equal(HttpStatusCode.Redirect, removed.StatusCode);
            Assert.True(probe.DeletionAccountLocked);
            Assert.Contains(removed.Headers.GetValues("Set-Cookie"), value => value.Contains(".AspNetCore.Identity.Application=;", StringComparison.Ordinal));
            output.WriteLine($"PostgreSQL observed self-service backend {waiter} waiting on first-use admission backend {holder}.");
        }
        finally
        {
            probe.ReleaseInsert.TrySetResult();
            probe.ReleaseContact.TrySetResult();
        }

        using var refused = await pending;
        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
        var problem = await refused.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mutation-authority-unavailable", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("state", out _));
        Assert.Equal(0, contact.Calls);
        Assert.False(await db.Users.AnyAsync(row => row.Id == user.Id));
        Assert.False(await db.GeoapifyUsageGuards.AnyAsync(row => row.UserId == user.Id));
        Assert.False(await db.GeoapifyUsageAdmissions.AnyAsync(row => row.UserId == user.Id));
        Assert.False(await db.Places.AnyAsync(row => row.UserId == user.Id));
        Assert.Equal(0L, await db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM public.trip_metadata_receipts WHERE subject_id = {user.Id}
            """).SingleAsync());
    }

    /// <summary>A competing first insert leaves the stamp free to rotate and late admission rolls its own meter back.</summary>
    [PostgresFact]
    public async Task StructureReceipt_MeterInsertWaitDoesNotBlockStampRotation()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        var region = await SeedStructureRegionAsync(db, trip);
        var freshness = await db.Trips.Where(row => row.Id == trip.Id).Select(row => row.UpdatedAt).SingleAsync();
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        await SeedProtectedProviderAsync(db, trip.UserId, credentials);
        var contact = new CountProviderContact();
        var probe = new FirstUseDeletionProbe(pauseForDeletion: false);
        await using var app = await StartProtectedProviderHostAsync(db, credentials, contact, probe);
        using var web = await WebAsync(app, trip.UserId);
        var (route, body, permit) = await IssuePlaceCreateAsync(web, trip, region);
        await using var competing = fixture.CreateContext();
        await using var transaction = await competing.Database.BeginTransactionAsync();
        await competing.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO public."GeoapifyUsageGuards" ("UserId", "Enabled", "CreditLimit") VALUES ({trip.UserId}, TRUE, 100)
            """);
        var holding = ((NpgsqlConnection)competing.Database.GetDbConnection()).ProcessID;
        var pending = TripMetadataReceiptHttp.SendAsync(web, HttpMethod.Post, route, permit, body);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var waiting = await probe.InsertReached.Task.WaitAsync(timeout.Token);
            await ObserveProviderBlockAsync(holding, waiting, timeout.Token);
            var stamp = Guid.NewGuid().ToString("N");
            Assert.Equal(1, await db.Users.Where(user => user.Id == trip.UserId)
                .ExecuteUpdateAsync(set => set.SetProperty(user => user.SecurityStamp, stamp), timeout.Token));
            output.WriteLine($"Stamp rotation committed while admission backend {waiting} waited on first-use backend {holding}.");
        }
        finally { await transaction.RollbackAsync(); }
        using var reply = await pending.WaitAsync(timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        var problem = await reply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mutation-authority-unavailable", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("operationId", out _));
        Assert.False(await db.GeoapifyUsageGuards.AnyAsync(row => row.UserId == trip.UserId));
        Assert.False(await db.GeoapifyUsageAdmissions.AnyAsync(row => row.UserId == trip.UserId));
        Assert.False(await db.Places.AnyAsync(row => row.UserId == trip.UserId));
        Assert.Equal(freshness, await db.Trips.Where(row => row.Id == trip.Id).Select(row => row.UpdatedAt).SingleAsync());
        Assert.Equal(0, contact.Calls);
        using var current = await WebAsync(app, trip.UserId);
        using var lookup = await LookupStructureAsync(current, route, permit);
        await TripMetadataReceiptHttp.EvidenceAsync(lookup, permit, "unobserved");
    }

    /// <summary>Uses the site's configured provider gate with a bounded marker catalogue and controlled HTTP.</summary>
    private Task<WebApplication> StartProtectedProviderHostAsync(ApplicationDbContext db,
        Wayfarer.Services.LocationProviders.PersonalProviderCredentialService credentials, CountProviderContact contact,
        params IInterceptor[] probes)
    {
        var webRoot = CreateTestDirectory();
        var assets = Path.Combine(webRoot, "icons", "wayfarer-map-icons", "dist");
        Directory.CreateDirectory(Path.Combine(assets, "marker"));
        File.WriteAllText(Path.Combine(assets, "marker", "marker.svg"), "<svg></svg>");
        File.WriteAllText(Path.Combine(assets, "wayfarer-map-icons.css"), ".bg-blue{} .color-white{}");
        return StartAsync(db, probes, servicesOverride: services =>
        {
            services.AddSingleton(credentials);
            services.AddSingleton<IWebHostEnvironment>(Mock.Of<IWebHostEnvironment>(environment => environment.WebRootPath == webRoot));
            services.AddSingleton<IIconColorProvider, IconColorProvider>();
            services.AddHttpClient<ReverseGeocodingService>().ConfigurePrimaryHttpMessageHandler(() => contact);
        });
    }

    /// <summary>Issues a typed permit for the Web Place create that uses the real reverse geocoding lane.</summary>
    private static async Task<(string Route, string Body, JsonElement Permit)> IssuePlaceCreateAsync(HttpClient web, Trip trip, Region region)
    {
        var route = $"/api/trips/{trip.Id}/editor/regions/{region.Id}/places/conditional/receipt";
        var body = JsonSerializer.Serialize(new
        {
            receiptContract = TripMetadataIdentity.StructureVersion,
            expected = new { regionId = region.Id, places = Array.Empty<object>() },
            value = new { name = "Location", notesHtml = (string?)null, address = (string?)null,
                location = new { latitude = 37.98, longitude = 23.72 }, iconName = "marker", markerColor = "bg-blue", reverseGeocode = true }
        });
        var identity = new TripMetadataIdentity(TripMetadataIdentity.StructureVersion, trip.UserId, "web", "place-create",
            trip.Id, "", Guid.Empty, default) { Target = new("region", region.Id) };
        var permit = await TripMetadataReceiptHttp.IssueTypedAsync(web, identity, route, body);
        return (route, body, permit);
    }

    /// <summary>Seeds current Geoapify authority without a first-use meter or an outbound contact.</summary>
    private static async Task SeedProtectedProviderAsync(ApplicationDbContext db, string user,
        Wayfarer.Services.LocationProviders.PersonalProviderCredentialService credentials,
        PersonalLocationProvider provider = PersonalLocationProvider.Geoapify)
    {
        var profile = PersonalLocationProviderProfile.Create(user, provider);
        credentials.Replace(profile, "controlled-provider");
        profile.SetAuthorization(PersonalProviderCapability.Geocoding, true);
        if (provider == PersonalLocationProvider.Mapbox) profile.GrantPermanentGeocodingConsent(DateTimeOffset.UtcNow);
        profile.GeocodingVerification = PersonalProviderVerification.Verified;
        profile.GeocodingVerifiedCredentialGeneration = profile.CredentialGeneration;
        profile.GeocodingVerifiedConfigurationGeneration = profile.GeocodingGeneration;
        var selection = PersonalLocationProviderSelection.Create(user);
        selection.Select(PersonalProviderCapability.Geocoding, provider);
        db.AddRange(profile, selection);
        await db.SaveChangesAsync();
    }

    /// <summary>Confirms the exact database backend as the blocker on the per-account fence.</summary>
    private async Task ObserveProviderBlockAsync(int holder, int waiter, CancellationToken token)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync(token);
        await using var query = new NpgsqlCommand("SELECT $1 = ANY(pg_blocking_pids($2))", connection);
        query.Parameters.AddWithValue(holder);
        query.Parameters.AddWithValue(waiter);
        while (await query.ExecuteScalarAsync(token) is not true)
            await Task.Delay(TimeSpan.FromMilliseconds(10), token);
    }

    /// <summary>Counts contacts against the controlled message handler used by the production resolver.</summary>
    private sealed class CountProviderContact : HttpMessageHandler
    {
        internal int Calls { get; private set; }

        /// <summary>Returns a controlled response without allowing a live provider contact.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }
    }

    /// <summary>Records actual PostgreSQL lock commands and gates the original request at the two provider boundaries.</summary>
    private sealed class FirstUseDeletionProbe(bool pauseForDeletion = true) : DbCommandInterceptor
    {
        private int _deletionBackend;
        private bool _installationReserved;
        internal bool DeletionAccountLocked { get; private set; }
        internal TaskCompletionSource<int> InsertReached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseInsert { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<int> DeletionFenceRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource BeforeContact { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseContact { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Pauses first insertion only after the production admission obtains its advisory fence.</summary>
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            var backend = ((NpgsqlConnection)command.Connection!).ProcessID;
            if (command.CommandText.Contains("INSERT INTO \"GeoapifyUsageGuards\"", StringComparison.Ordinal))
            {
                InsertReached.TrySetResult(backend);
                if (pauseForDeletion) await ReleaseInsert.Task.WaitAsync(token);
            }
            if (command.CommandText.Contains("pg_advisory_xact_lock(887, 1)", StringComparison.Ordinal))
            {
                _deletionBackend = backend;
                _installationReserved = true;
            }
            if (backend == _deletionBackend && _installationReserved && command.CommandText.Contains("hashtextextended", StringComparison.Ordinal))
                DeletionFenceRequested.TrySetResult(backend);
            return result;
        }

        /// <summary>Lets deletion commit before the original invocation's provider-authority reread.</summary>
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            var backend = ((NpgsqlConnection)command.Connection!).ProcessID;
            if (backend == _deletionBackend && command.CommandText.Contains("AspNetUsers", StringComparison.Ordinal)
                && command.CommandText.Contains("FOR UPDATE", StringComparison.Ordinal))
                DeletionAccountLocked = DeletionFenceRequested.Task.IsCompletedSuccessfully;
            if (ReleaseInsert.Task.IsCompleted && command.Transaction == null && command.CommandText.Contains("PersonalLocationProviderSelections",
                StringComparison.Ordinal) && backend != _deletionBackend)
            {
                BeforeContact.TrySetResult();
                await ReleaseContact.Task.WaitAsync(token);
            }
            return result;
        }
    }
}
