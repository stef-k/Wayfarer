using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Builder;
using Moq;
using Quartz;
using Wayfarer.Models;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripCompositionHttpPostgresTests
{
    /// <summary>Cookie CSRF and incoming bearer authority protect the same atomic copy operation and public freshness effects.</summary>
    [PostgresFact]
    public async Task CookieAndBearerCopyUseSameOperationWithoutWorkspaceOrImplicitMetadata()
    {
        var owner = await fixture.CreateUserAsync(); var foreign = await fixture.CreateUserAsync();
        var destination = Source(owner.Id, "Existing destination", true, "CC0-1.0", 23);
        var source = Source(foreign.Id, "Reusable source", true, "CC-BY-4.0", 24);
        fixture.RegisterTrip(source.Id); fixture.RegisterTrip(destination.Id);
        await using var db = fixture.CreateContext();
        db.AddRange(source, destination, new ApiToken { UserId = owner.Id, User = await db.Users.SingleAsync(u => u.Id == owner.Id), Token = owner.Id, Name = "copy" }); await db.SaveChangesAsync();
        var thumbnails = new Mock<ITripMapThumbnailGenerator>(); var warmup = new Mock<ICacheWarmupScheduler>();
        warmup.Setup(s => s.ScheduleWarmupAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
            services.AddSingleton(thumbnails.Object); services.AddSingleton(warmup.Object); services.AddSingleton(Mock.Of<IIconColorProvider>());
        });
        using var browser = app.GetTestClient(); using var mobile = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(browser, app.Services, owner.Id);
        browser.DefaultRequestHeaders.AcceptLanguage.ParseAdd("el-GR");
        mobile.DefaultRequestHeaders.Authorization = new("Bearer", owner.Id);
        var url = $"/api/trip-compositions/copy-preview?sourceTripId={source.Id}&destinationTripId={destination.Id}";
        using var previewResponse = await browser.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, previewResponse.StatusCode); Assert.True(previewResponse.Headers.CacheControl!.NoStore);
        var preview = await previewResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(preview.GetProperty("source").TryGetProperty("planning", out _));
        using var wrongAuthority = await browser.GetAsync(url.Replace("trip-compositions", "trips"));
        Assert.Equal(HttpStatusCode.Unauthorized, wrongAuthority.StatusCode);
        var parent = destination.Regions.Single(); var target = parent.Places.Single(); var place = source.Regions.Single().Places.Single();
        var request = new { sourceTripId = source.Id, destinationTripId = destination.Id, context = preview.GetProperty("context"), originals = preview.GetProperty("originals"),
            items = new[] { new { kind = "place", sourceId = place.Id, decision = "copy", regionId = parent.Id, name = "Copied idea",
                placement = new { expected = new[] { new { id = target.Id, displayOrder = target.DisplayOrder } }, position = "after", afterId = target.Id } } } };
        using var noCsrf = await browser.PostAsJsonAsync("/api/trip-compositions/copy", request);
        Assert.Equal(HttpStatusCode.BadRequest, noCsrf.StatusCode);
        using var cookieOnly = await browser.PostAsJsonAsync("/api/trips/copy", request);
        Assert.Equal(HttpStatusCode.Unauthorized, cookieOnly.StatusCode);
        using var copied = await mobile.PostAsJsonAsync("/api/trips/copy", request);
        Assert.True(copied.StatusCode == HttpStatusCode.OK, await copied.Content.ReadAsStringAsync()); Assert.True(copied.Headers.CacheControl!.NoStore);
        var result = await copied.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(destination.Id, result.GetProperty("tripId").GetGuid()); Assert.Equal("copied", result.GetProperty("status").GetString());
        Assert.Equal(2, result.GetProperty("current").GetProperty("graph").GetProperty("regions")[0].GetProperty("places").GetArrayLength());
        Assert.Equal("CC0-1.0", result.GetProperty("current").GetProperty("graph").GetProperty("contributionPolicy").GetString());
        thumbnails.Verify(t => t.InvalidateThumbnails(destination.Id, It.IsAny<DateTime>()), Times.Once);
        warmup.Verify(t => t.ScheduleWarmupAsync(destination.Id), Times.Once);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(browser);
        using var newPreviewResponse = await browser.GetAsync(url);
        var newPreview = await newPreviewResponse.Content.ReadFromJsonAsync<JsonElement>();
        const string authoredNotes = "<p>Μονοπάτι &amp; Trail</p>";
        var reuse = new { sourceTripId = source.Id, destinationTripId = destination.Id, context = newPreview.GetProperty("context"), originals = newPreview.GetProperty("originals"),
            items = new[] { new { kind = "place", sourceId = place.Id, decision = "reuse", destinationId = target.Id, expectedParentId = parent.Id } } };
        using var reused = await browser.PostAsJsonAsync("/api/trip-compositions/copy", new
        {
            reuse.sourceTripId, reuse.destinationTripId, reuse.context, reuse.originals,
            items = new[] { new {
                kind = "place", sourceId = place.Id, decision = "reuse", destinationId = target.Id, expectedParentId = parent.Id,
                fields = new { notes = new { operation = "edit", expected = (string?)null, value = authoredNotes } }
            } }
        });
        Assert.True(reused.StatusCode == HttpStatusCode.OK, await reused.Content.ReadAsStringAsync());
        var persisted = await db.Places.AsNoTracking().SingleAsync(p => p.Id == target.Id);
        Assert.Equal(authoredNotes, persisted.Notes);
        Assert.Equal(parent.Id, persisted.RegionId);
        Assert.Null((await db.Places.AsNoTracking().SingleAsync(p => p.Id == place.Id)).Notes);
        thumbnails.Verify(t => t.InvalidateThumbnails(destination.Id, It.IsAny<DateTime>()), Times.Exactly(2));
        Assert.False(await db.Set<TripComposition>().AnyAsync(w => w.TripId == destination.Id));
        Assert.Single(await db.Places.Where(p => p.Region.TripId == source.Id).ToListAsync());
        await db.ApiTokens.Where(t => t.UserId == owner.Id).ExecuteDeleteAsync();
        using var revoked = await mobile.PostAsJsonAsync("/api/trips/copy", reuse); Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
    }

    /// <summary>Real bearer/native transport preserves complete portable content, exact measurements and explicit owner planning.</summary>
    [PostgresFact]
    public async Task BearerNativeDownloadCreateAndReviewedReplacementShareV5Pipeline()
    {
        var owner = await fixture.CreateUserAsync();
        var source = Source(owner.Id, "Native transport", false, null, 24);
        source.Planning = new(1, [new(Guid.NewGuid(), new("unknown"), "owner planning")]);
        source.Credits = [new("Prior author", "Offline ideas", "Other supplied terms", Notices: "Keep notice")];
        var place = source.Regions.Single().Places.Single();
        var route = new Segment { Id = Guid.NewGuid(), TripId = source.Id, UserId = owner.Id, Title = "Optional title", Notes = "<p>Route Notes</p>", Mode = "walk",
            FromPlaceId = place.Id, ToPlaceId = null, EstimatedDurationSource = EstimatedDurationSource.Manual, EstimatedDuration = TimeSpan.FromTicks(12345670), EstimatedDistanceKm = 1.23456789 };
        source.Segments.Add(route); fixture.RegisterTrip(source.Id);
        await using var db = fixture.CreateContext(); db.AddRange(source, new ApiToken { UserId = owner.Id, User = await db.Users.SingleAsync(u => u.Id == owner.Id), Token = owner.Id, Name = "native" }); await db.SaveChangesAsync();
        // Both derived effects can fail after a known commit without changing the saved result.
        var thumbnails = new Mock<ITripMapThumbnailGenerator>(); var warmup = new Mock<ICacheWarmupScheduler>();
        thumbnails.Setup(t => t.InvalidateThumbnails(It.IsAny<Guid>(), It.IsAny<DateTime>())).Throws(new InvalidOperationException("Thumbnail unavailable"));
        warmup.Setup(t => t.ScheduleWarmupAsync(It.IsAny<Guid>())).ThrowsAsync(new InvalidOperationException("Warmup unavailable"));
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext()); services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture)); services.AddSingleton(Mock.Of<IIconColorProvider>());
            services.AddSingleton(thumbnails.Object); services.AddSingleton(warmup.Object);
        });
        using var cookie = app.GetTestClient(); UserConnectionTokenHttpTests.Cookie(cookie, app.Services, owner.Id);
        using var denied = await cookie.GetAsync($"/api/trips/{source.Id}/native?includePrivatePlanning=true"); Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        using var mobile = app.GetTestClient(); mobile.DefaultRequestHeaders.Authorization = new("Bearer", owner.Id);
        using var defaultDownload = await mobile.GetAsync($"/api/trips/{source.Id}/native");
        Assert.True(defaultDownload.StatusCode == HttpStatusCode.OK, await defaultDownload.Content.ReadAsStringAsync()); Assert.True(defaultDownload.Headers.CacheControl!.NoStore);
        var defaultXml = await defaultDownload.Content.ReadAsStringAsync(); Assert.DoesNotContain("Planning", defaultXml);
        using var personal = await mobile.GetAsync($"/api/trips/{source.Id}/native?includePrivatePlanning=true");
        Assert.Equal(HttpStatusCode.OK, personal.StatusCode); var xml = await personal.Content.ReadAsStringAsync();
        Assert.Contains("Planning", xml); Assert.Contains("Optional title", xml); Assert.Contains("Keep notice", xml);
        using var imported = await mobile.PostAsync("/api/trips/native/import", Upload(xml, "CreateNew"));
        Assert.True(imported.StatusCode == HttpStatusCode.OK, await imported.Content.ReadAsStringAsync());
        var result = await imported.Content.ReadFromJsonAsync<JsonElement>(); var id = result.GetProperty("tripId").GetGuid(); fixture.RegisterTrip(id);
        Assert.True(result.GetProperty("currentAvailable").GetBoolean());
        thumbnails.Verify(t => t.InvalidateThumbnails(id, It.IsAny<DateTime>()), Times.Once);
        warmup.Verify(t => t.ScheduleWarmupAsync(id), Times.Once);
        Assert.NotEqual(source.Id, id);
        var saved = await db.Trips.AsNoTracking().Include(t => t.Regions).ThenInclude(r => r.Places).Include(t => t.Segments).SingleAsync(t => t.Id == id);
        Assert.False(saved.IsPublic); Assert.Null(saved.ContributionPolicy); Assert.Equal(source.Credits, saved.Credits);
        Assert.NotEqual(source.Planning!.Entries[0].Id, saved.Planning!.Entries[0].Id);
        var copied = saved.Segments.Single(); Assert.Equal(route.Title, copied.Title); Assert.Equal(route.Notes, copied.Notes);
        Assert.Equal(route.EstimatedDuration, copied.EstimatedDuration); Assert.Equal(route.EstimatedDistanceKm, copied.EstimatedDistanceKm);
        Assert.Null(copied.ToPlaceId); Assert.NotEqual(place.Id, copied.FromPlaceId);
        using var review = await mobile.PostAsync("/api/trips/native/review", Upload(xml, "Upsert"));
        Assert.True(review.StatusCode == HttpStatusCode.OK, await review.Content.ReadAsStringAsync());
        var reviewed = await review.Content.ReadFromJsonAsync<JsonElement>();
        var replacement = Upload(xml, "Upsert"); replacement.Add(new StringContent(reviewed.GetProperty("review").GetProperty("token").GetString()!), "reviewToken");
        using var replaced = await mobile.PostAsync("/api/trips/native/import", replacement);
        Assert.True(replaced.StatusCode == HttpStatusCode.OK, await replaced.Content.ReadAsStringAsync());
        using var malformed = await mobile.PostAsync("/api/trips/native/import", Upload("<kml><Document><name>Generic</name></Document></kml>", "CreateNew"));
        Assert.Equal(HttpStatusCode.UnprocessableEntity, malformed.StatusCode);
        using var ownerRead = await mobile.GetAsync($"/api/trips/{source.Id}/planning"); Assert.Equal(HttpStatusCode.OK, ownerRead.StatusCode);
    }


    /// <summary>A real native commit remains a success when ordinary lifecycle deletion wins before the response.</summary>
    [PostgresFact]
    public async Task NativeCommittedThenDeletedReturnsSavedCurrentlyUnavailable()
    {
        var owner = await fixture.CreateUserAsync();
        var source = Source(owner.Id, "Postcommit deletion", false, null, 24);
        fixture.RegisterTrip(source.Id);
        await using var db = fixture.CreateContext();
        db.AddRange(source, new ApiToken { UserId = owner.Id, User = await db.Users.SingleAsync(u => u.Id == owner.Id), Token = owner.Id, Name = "native" });
        await db.SaveChangesAsync();
        var deletion = new DeleteCommittedNative(fixture, owner.Id, source.Id);
        var thumbnails = new Mock<ITripMapThumbnailGenerator>(); var warmup = new Mock<ICacheWarmupScheduler>();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext(deletion));
            services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
            services.AddSingleton(thumbnails.Object); services.AddSingleton(warmup.Object); services.AddSingleton(Mock.Of<IIconColorProvider>());
        });
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Id);
        using var response = await client.PostAsync("/api/trips/native/import", Upload(Wayfarer.Parsers.TripWayfarerKmlExporter.BuildKml(source), "CreateNew"));
        Assert.NotEqual(Guid.Empty, deletion.DeletedId);
        Assert.False(await db.Trips.AsNoTracking().AnyAsync(t => t.Id == deletion.DeletedId));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("success", body.GetProperty("status").GetString());
        Assert.Equal(deletion.DeletedId, body.GetProperty("tripId").GetGuid());
        Assert.False(body.GetProperty("currentAvailable").GetBoolean());
        Assert.Contains("saved", body.GetProperty("detail").GetString());
        Assert.Contains("Inspect", body.GetProperty("detail").GetString());
        thumbnails.Verify(t => t.InvalidateThumbnails(It.IsAny<Guid>(), It.IsAny<DateTime>()), Times.Never);
        warmup.Verify(t => t.ScheduleWarmupAsync(It.IsAny<Guid>()), Times.Never);
    }

    /// <summary>Observes the independently visible committed row, then uses ordinary Trip deletion before adapter work.</summary>
    private sealed class DeleteCommittedNative(PostgresImportTestFixture fixture, string owner, Guid source) : DbTransactionInterceptor
    {
        internal Guid DeletedId;
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        {
            if (DeletedId != Guid.Empty) return;
            await using var db = fixture.CreateContext();
            var id = await db.Trips.Where(t => t.UserId == owner && t.Id != source).Select(t => t.Id).SingleAsync(token);
            DeletedId = id; fixture.RegisterTrip(id);
            Assert.True(await TripCompositionLifecycle.DeleteAsync(db, id, owner, token));
        }
    }

    private static MultipartFormDataContent Upload(string xml, string mode)
    {
        var body = new MultipartFormDataContent();
        body.Add(new StringContent(xml, Encoding.UTF8, "application/vnd.google-earth.kml+xml"), "file", "trip.kml");
        body.Add(new StringContent(mode), "mode"); return body;
    }

    /// <summary>Real imports preserve saved truth through failed inspection and independent derived-effect failures.</summary>
    [PostgresTheory]
    [InlineData("inspection")]
    [InlineData("cancelled-inspection")]
    [InlineData("thumbnail")]
    [InlineData("warmup")]
    [InlineData("both")]
    [InlineData("request-cancelled-inspection")]
    public async Task NativePostCommitFailuresPreserveSavedTruthWithoutPrivateDiagnostics(string fault)
    {
        var owner = await fixture.CreateUserAsync(); var source = Source(owner.Id, "Review saved truth", false, null, 24);
        fixture.RegisterTrip(source.Id);
        await using var db = fixture.CreateContext();
        db.AddRange(source, new ApiToken { UserId = owner.Id, User = await db.Users.SingleAsync(u => u.Id == owner.Id), Token = owner.Id, Name = "review" }); await db.SaveChangesAsync();
        using var logs = new TestLogProvider();
        const string sentinel = "PRIVATE_NATIVE_DIAGNOSTIC";
        using var requestCancellation = new CancellationTokenSource();
        var inspection = new NativeInspectionFault(fixture, owner.Id, source.Id, fault, sentinel, requestCancellation);
        // Inject private diagnostics in every part a logger might serialize, not just the response body.
        Exception diagnostic = new InvalidOperationException(sentinel, new IOException(sentinel + "_INNER"));
        diagnostic.Data[sentinel + "_KEY"] = sentinel + "_DATA";
        var thumbnails = new Mock<ITripMapThumbnailGenerator>(); var warmup = new Mock<ICacheWarmupScheduler>();
        if (fault is "thumbnail" or "both") thumbnails.Setup(t => t.InvalidateThumbnails(It.IsAny<Guid>(), It.IsAny<DateTime>())).Throws(diagnostic);
        if (fault is "warmup" or "both") warmup.Setup(t => t.ScheduleWarmupAsync(It.IsAny<Guid>())).ThrowsAsync(diagnostic);
        else warmup.Setup(t => t.ScheduleWarmupAsync(It.IsAny<Guid>())).Returns(Task.CompletedTask);
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddLogging(l => l.AddProvider(logs).AddFilter(typeof(Wayfarer.Areas.Api.Controllers.BaseApiController).FullName, LogLevel.Debug));
            services.AddScoped(_ => fixture.CreateContext(inspection));
            services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
            services.AddSingleton(thumbnails.Object); services.AddSingleton(warmup.Object); services.AddSingleton(Mock.Of<IIconColorProvider>());
        }, configureAuthorized: host => host.Use(async (context, next) =>
        {
            context.RequestAborted = requestCancellation.Token;
            await next();
        }));
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Id);
        using var response = await client.PostAsync("/api/trips/native/import", Upload(TripWayfarerKmlExporter.BuildKml(source), "CreateNew"));
        var text = await response.Content.ReadAsStringAsync(); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = JsonDocument.Parse(text).RootElement; Assert.Equal("success", body.GetProperty("status").GetString());
        var id = body.GetProperty("tripId").GetGuid(); fixture.RegisterTrip(id);
        var persisted = await db.Trips.AsNoTracking().SingleAsync(t => t.Id == id); Assert.Equal(owner.Id, persisted.UserId);
        Assert.Equal(2, await db.Trips.CountAsync(t => t.UserId == owner.Id)); Assert.DoesNotContain(sentinel, text);
        if (fault.Contains("inspection")) { Assert.Equal(JsonValueKind.Null, body.GetProperty("currentAvailable").ValueKind);
            Assert.Equal(id, inspection.ObservedCommitted); Assert.Contains("saved", body.GetProperty("detail").GetString());
            thumbnails.Verify(t => t.InvalidateThumbnails(It.IsAny<Guid>(), It.IsAny<DateTime>()), Times.Never);
            warmup.Verify(t => t.ScheduleWarmupAsync(It.IsAny<Guid>()), Times.Never); }
        else { Assert.True(body.GetProperty("currentAvailable").GetBoolean());
            thumbnails.Verify(t => t.InvalidateThumbnails(id, persisted.UpdatedAt), Times.Once);
            warmup.Verify(t => t.ScheduleWarmupAsync(id), Times.Once); }
        var adapterLogs = logs.Entries.Where(e => e.Category == typeof(Wayfarer.Areas.Api.Controllers.BaseApiController).FullName).ToList();
        Assert.Equal(fault == "both" ? 2 : 1, adapterLogs.Count);
        Assert.Equal(fault.Contains("inspection") ? new[] { "inspection" } : fault == "both" ? new[] { "thumbnail", "warmup" } : new[] { fault },
            adapterLogs.Select(e => e.Fields["Phase"]));
        foreach (var entry in adapterLogs)
        {
            Assert.Null(entry.Exception);
            Assert.DoesNotContain(sentinel, entry.Message);
            Assert.DoesNotContain(sentinel, JsonSerializer.Serialize(entry.Fields));
            Assert.Equal(new[] { "ExceptionType", "Phase", "TripId", "UserId" }, entry.Fields.Keys.Order());
            Assert.Equal(id, entry.Fields["TripId"]); Assert.Equal(owner.Id, entry.Fields["UserId"]);
            Assert.Equal(fault.Contains("cancelled") ? nameof(OperationCanceledException) : nameof(InvalidOperationException), entry.Fields["ExceptionType"]);
            Assert.Equal(fault == "request-cancelled-inspection" ? LogLevel.Debug : LogLevel.Warning, entry.Level);
        }
    }

    /// <summary>Faults only the post-commit inspection, after observing persisted success independently.</summary>
    private sealed class NativeInspectionFault(PostgresImportTestFixture fixture, string owner, Guid source, string fault, string message, CancellationTokenSource cancellation) : DbCommandInterceptor
    {
        internal Guid ObservedCommitted;
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (fault.Contains("inspection") && ObservedCommitted == Guid.Empty && data.Context?.Database.CurrentTransaction == null
                && command.CommandText.Contains("FROM \"Trips\""))
            {
                await using var observer = fixture.CreateContext();
                ObservedCommitted = await observer.Trips.Where(t => t.UserId == owner && t.Id != source).Select(t => t.Id).SingleAsync();
                fixture.RegisterTrip(ObservedCommitted);
                if (fault == "request-cancelled-inspection") cancellation.Cancel();
                Exception failure = fault.Contains("cancelled") ? new OperationCanceledException(message, new IOException(message + "_INNER"), token)
                    : new InvalidOperationException(message, new IOException(message + "_INNER"));
                failure.Data[message + "_KEY"] = message + "_DATA";
                throw failure;
            }
            return result;
        }
    }

    /// <summary>Real effect owners keep useful bounded warnings and confirmed save truth when both effects fail internally.</summary>
    [PostgresFact]
    public async Task NativeRealDerivedEffectFailuresPreserveSavedTruthWithoutPrivateDiagnostics()
    {
        var owner = await fixture.CreateUserAsync();
        var source = Source(owner.Id, "Real derived owners", false, null, 24);
        fixture.RegisterTrip(source.Id);
        await using var db = fixture.CreateContext();
        db.AddRange(source, new ApiToken { UserId = owner.Id, User = await db.Users.SingleAsync(u => u.Id == owner.Id), Token = owner.Id, Name = "native" });
        await db.SaveChangesAsync();
        using var logs = new TestLogProvider();
        using var loggerFactory = LoggerFactory.Create(l => l.AddProvider(logs));
        const string sentinel = "PRIVATE_NATIVE_EFFECT_DIAGNOSTIC";
        var privateRoot = Path.Combine(CreateTestDirectory(), sentinel);
        var storage = new TripThumbnailStorage(TestDirectory.Storage(privateRoot));
        var thumbnails = new TripMapThumbnailGenerator(loggerFactory.CreateLogger<TripMapThumbnailGenerator>(),
            storage, new ConfigurationBuilder().Build());
        // A real filesystem failure occurs after initialization, inside InvalidateThumbnails.
        Directory.Delete(storage.Root);
        Exception diagnostic;
        try { throw new IOException(sentinel, new InvalidOperationException(sentinel + "_INNER")); }
        catch (IOException captured) { diagnostic = captured; }
        diagnostic.Data[sentinel + "_KEY"] = sentinel + "_DATA";
        var scheduler = new Mock<IScheduler>();
        scheduler.Setup(s => s.CheckExists(It.IsAny<TriggerKey>(), It.IsAny<CancellationToken>())).ThrowsAsync(diagnostic);
        var warmup = new CacheWarmupScheduler(scheduler.Object, loggerFactory.CreateLogger<CacheWarmupScheduler>());
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddLogging(l => l.AddProvider(logs));
            services.AddScoped(_ => fixture.CreateContext());
            services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
            services.AddSingleton<ITripMapThumbnailGenerator>(thumbnails);
            services.AddSingleton<ICacheWarmupScheduler>(warmup);
            services.AddSingleton(Mock.Of<IIconColorProvider>());
        });
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Id);
        using var response = await client.PostAsync("/api/trips/native/import", Upload(TripWayfarerKmlExporter.BuildKml(source), "CreateNew"));
        var text = await response.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(text);
        var body = document.RootElement;
        Assert.Equal("success", body.GetProperty("status").GetString());
        var id = body.GetProperty("tripId").GetGuid();
        fixture.RegisterTrip(id);
        Assert.NotEqual(source.Id, id);
        Assert.True(body.GetProperty("currentAvailable").GetBoolean());
        Assert.Contains("saved", body.GetProperty("detail").GetString());
        Assert.True(await db.Trips.AsNoTracking().AnyAsync(t => t.Id == id && t.UserId == owner.Id));
        Assert.Equal(2, await db.Trips.CountAsync(t => t.UserId == owner.Id));
        Assert.DoesNotContain(sentinel, text);
        scheduler.Verify(s => s.CheckExists(new TriggerKey($"CacheWarmup-{id}", "CacheWarmup"), It.IsAny<CancellationToken>()), Times.Once);
        // Capture both actual service categories, rather than only exceptions escaping to the adapter.
        var effects = logs.Entries.Where(l => l.Level >= LogLevel.Warning &&
            (l.Category == typeof(TripMapThumbnailGenerator).FullName || l.Category == typeof(CacheWarmupScheduler).FullName)).ToArray();
        Assert.Equal(2, effects.Length);
        foreach (var (category, failureType, message) in new[]
        {
            (typeof(TripMapThumbnailGenerator).FullName, nameof(DirectoryNotFoundException), $"Failed to invalidate thumbnails for trip {id} ({nameof(DirectoryNotFoundException)})"),
            (typeof(CacheWarmupScheduler).FullName, nameof(IOException), $"Failed to schedule cache warm-up for trip {id} ({nameof(IOException)}).")
        })
        {
            var entry = Assert.Single(effects, l => l.Category == category);
            Assert.Equal(LogLevel.Warning, entry.Level);
            Assert.Equal(message, entry.Message);
            Assert.Null(entry.Exception);
            Assert.Equal(new[] { "FailureType", "TripId" }, entry.Fields.Keys.Order());
            Assert.Equal(id, entry.Fields["TripId"]);
            Assert.Equal(failureType, entry.Fields["FailureType"]);
            Assert.DoesNotContain(sentinel, entry.Message + JsonSerializer.Serialize(entry.Fields));
            Assert.DoesNotContain(privateRoot, entry.Message + JsonSerializer.Serialize(entry.Fields));
        }
    }

    /// <summary>Lost commit acknowledgement remains unconfirmed even when the independent observer sees the save.</summary>
    [PostgresFact]
    public async Task NativeCommitAcknowledgementLossDoesNotBecomeValidationRefusalOrReplay()
    {
        var owner = await fixture.CreateUserAsync(); var source = Source(owner.Id, "Review lost acknowledgement", false, null, 24);
        fixture.RegisterTrip(source.Id); await using var db = fixture.CreateContext();
        db.AddRange(source, new ApiToken { UserId = owner.Id, User = await db.Users.SingleAsync(u => u.Id == owner.Id), Token = owner.Id, Name = "review" }); await db.SaveChangesAsync();
        var loss = new NativeCommitLoss(fixture, owner.Id, source.Id);
        var thumbnails = new Mock<ITripMapThumbnailGenerator>(); var warmup = new Mock<ICacheWarmupScheduler>();
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory(), services =>
        {
            services.AddScoped(_ => fixture.CreateContext(loss)); services.AddSingleton<IDbContextFactory<ApplicationDbContext>>(new Factory(fixture));
            services.AddSingleton(thumbnails.Object); services.AddSingleton(warmup.Object); services.AddSingleton(Mock.Of<IIconColorProvider>());
        });
        using var client = app.GetTestClient(); client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Id);
        using var response = await client.PostAsync("/api/trips/native/import", Upload(TripWayfarerKmlExporter.BuildKml(source), "CreateNew"));
        Assert.NotEqual(Guid.Empty, loss.Committed); Assert.True(await db.Trips.AnyAsync(t => t.Id == loss.Committed));
        Assert.Equal(2, await db.Trips.CountAsync(t => t.UserId == owner.Id)); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(); var body = JsonDocument.Parse(text).RootElement;
        Assert.Contains(body.GetProperty("code").GetString(), new[] { "native-import-unconfirmed", "composition-save-unconfirmed" });
        Assert.DoesNotContain("not applied", text); Assert.DoesNotContain("PRIVATE_ACK_DIAGNOSTIC", text);
        thumbnails.Verify(t => t.InvalidateThumbnails(It.IsAny<Guid>(), It.IsAny<DateTime>()), Times.Never);
        warmup.Verify(t => t.ScheduleWarmupAsync(It.IsAny<Guid>()), Times.Never);
    }
    /// <summary>Throws after independently observing the committed row to model lost commit acknowledgement.</summary>
    private sealed class NativeCommitLoss(PostgresImportTestFixture fixture, string owner, Guid source) : DbTransactionInterceptor
    {
        internal Guid Committed;
        public override async Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken token = default)
        {
            await using var observer = fixture.CreateContext();
            Committed = await observer.Trips.Where(t => t.UserId == owner && t.Id != source).Select(t => t.Id).SingleAsync();
            fixture.RegisterTrip(Committed); throw new IOException("PRIVATE_ACK_DIAGNOSTIC");
        }
    }
}
