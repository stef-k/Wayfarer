using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Wayfarer.Middleware;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Point = NetTopologySuite.Geometries.Point;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Small durable fixtures exercise serving-profile transitions at existing writer/read/preparation seams.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class HistoryQualificationPostgresTests(PostgresMigrationTestFixture fixture) : TestBase, IClassFixture<PostgresMigrationTestFixture>
{
    /// <summary>Reviewed runtime servicing preserves current rows, active state, partial checkpoints and continuations.</summary>
    [PostgresFact]
    public async Task CompatibleServicingKeepsRowsReadinessAndResumeStateWithoutPreparing()
    {
        var owner = await fixture.CreateUserAsync();
        var previous = HistoryDerivationEnvironment.Current with { Runtime = new Version(10, 0, 13) };
        var serviced = previous with { Runtime = new Version(10, 0, 14) };
        Assert.True(previous.TextQualified); Assert.True(serviced.TextQualified);
        using var beforeServices = new ServiceCollection().AddSingleton(previous).BuildServiceProvider();
        using var afterServices = new ServiceCollection().AddSingleton(serviced).BuildServiceProvider();
        await using var before = Context(beforeServices);
        before.Add(Row(owner.Id)); await before.SaveChangesAsync();
        var preparation = new LocationHistoryPreparation(before);
        foreach (var kind in new[] { "time", "text" })
        {
            await preparation.BeginAsync(kind, true, default);
            while (!await preparation.PrepareBatchAsync(kind, default)) { }
        }
        var rows = await RowsAsync(before);
        var states = await StatesAsync(before);
        var cursors = new HistoryCursor(new EphemeralDataProtectionProvider());
        var query = HistoryQueryTests.Parse("?timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=2025-01-01T00:00:00Z&endUtc=2026-01-01T00:00:00Z&q=café");
        var key = HistoryCursor.ScopeKey(owner.Id, query.Scope);
        var cursor = cursors.Protect(owner.Id, query, key, 1, new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc));
        await using var after = Context(afterServices);
        var upgraded = new LocationHistoryPreparation(after);
        Assert.True(await upgraded.IsReadyAsync(true, true, default));
        foreach (var kind in new[] { "time", "text" }) Assert.True((await upgraded.StatusAsync(kind, default)).Ready);
        var continued = new HistoryQuery { Scope = query.Scope, Zone = query.Zone, Start = query.Start, End = query.End, Continuation = cursor };
        await new HistoryReadService(after, cursors).PrepareQueryAsync(owner.Id, continued, default);
        Assert.Equal(key, HistoryCursor.ScopeKey(owner.Id, continued.Scope));
        Assert.Equal(LocationHistoryDerivation.NormalizeText("café", previous), LocationHistoryDerivation.NormalizeText("café", serviced));
        Assert.Equal(rows, await RowsAsync(after)); Assert.Equal(states, await StatesAsync(after));
        await preparation.BeginAsync("time", true, default);
        await preparation.PrepareBatchAsync("time", default, 1);
        var checkpoint = await StatesAsync(before);
        await upgraded.BeginAsync("time", false, default);
        Assert.Equal(checkpoint, await StatesAsync(after));
        Assert.False((await upgraded.StatusAsync("time", default)).Ready);
        Assert.True((await upgraded.StatusAsync("text", default)).Ready);
        while (!await upgraded.PrepareBatchAsync("time", default)) { }
        Assert.True(await upgraded.IsReadyAsync(true, true, default));
    }

    /// <summary>Real old-main generations need explicit, independent rebuilding; mixed other-owner rows remain pending.</summary>
    [PostgresFact]
    public async Task OldGenerationsRebuildIndependentlyAndSweepOtherOwnerRowsBelowCheckpoint()
    {
        const string oldTime = "history-time-1;GeoTimeZone-6.1.0;TZDB: 2026c (mapping: 48.2)";
        const string oldText = "history-text-1;RichNotes-2;AngleSharp-1.7.1.0;runtime-10.0.13;unicode-31129-0000799900000000000000000000007f";
        var owner = await fixture.CreateUserAsync(); var other = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext(); var row = Row(other.Id); db.AddRange(Row(owner.Id), row); await db.SaveChangesAsync();
        var preparation = new LocationHistoryPreparation(db);
        foreach (var kind in new[] { "time", "text" })
        {
            await preparation.BeginAsync(kind, true, default);
            while (!await preparation.PrepareBatchAsync(kind, default)) { }
        }
        var originals = await db.Locations.AsNoTracking().SingleAsync(item => item.Id == row.Id);
        await db.Locations.Where(item => item.Id == row.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(item => item.HistoryTimeVersion, oldTime).SetProperty(item => item.HistoryTextVersion, oldText)
            .SetProperty(item => item.CaptureOffsetSeconds, 3600)
            .SetProperty(item => item.CaptureLocalTimestamp, new DateTime(1925, 9, 25, 13, 0, 0))
            .SetProperty(item => item.HistorySearchText, "SECRET VISIBLE"));
        await db.HistoryPreparationStates.ExecuteUpdateAsync(setters => setters
            .SetProperty(state => state.TargetVersion, state => state.Kind == "time" ? oldTime : oldText)
            .SetProperty(state => state.ActiveVersion, state => state.Kind == "time" ? oldTime : oldText));
        Assert.False(await preparation.IsReadyAsync(true, false, default));
        Assert.False(await preparation.IsReadyAsync(false, true, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.BeginAsync("time", false, default));
        await preparation.BeginAsync("time", true, default);
        while (!await preparation.PrepareBatchAsync("time", default)) { }
        Assert.True(await preparation.IsReadyAsync(true, false, default));
        Assert.False(await preparation.IsReadyAsync(false, true, default));
        Assert.Equal(oldText, await db.Locations.Where(item => item.Id == row.Id).Select(item => item.HistoryTextVersion).SingleAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => preparation.BeginAsync("text", false, default));
        await preparation.BeginAsync("text", true, default);
        while (!await preparation.PrepareBatchAsync("text", default)) { }
        foreach (var stale in new string?[] { oldText, null, "history-text-1;arbitrary" })
        {
            await db.Locations.Where(item => item.Id == row.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.HistoryTextVersion, stale)
                .SetProperty(item => item.HistorySearchText, stale == null ? null : "VISIBLE\nCAFE ROAD"));
            Assert.False(await preparation.IsReadyAsync(false, true, default));
            Assert.True(await preparation.IsReadyAsync(true, false, default));
            await preparation.BeginAsync("text", false, default);
            while (!await preparation.PrepareBatchAsync("text", default)) { }
        }
        var final = await db.Locations.AsNoTracking().SingleAsync(item => item.Id == row.Id);
        Assert.Equal("VISIBLE\nCAFE ROAD", final.HistorySearchText); Assert.Equal(0, final.CaptureOffsetSeconds);
        Assert.Equal(originals.LocalTimestamp.Ticks, final.CaptureLocalTimestamp!.Value.Ticks);
        foreach (var property in db.Entry(originals).Properties.Where(property => !LocationHistoryDerivation.IsDerived(property.Metadata.Name)))
            Assert.Equal(property.CurrentValue, db.Entry(final).Property(property.Metadata.Name).CurrentValue);
        Assert.True(originals.Coordinates.EqualsExact(final.Coordinates));
    }

    /// <summary>Unsupported environments neither stamp nor activate, and do not erase valid durable state.</summary>
    [PostgresFact]
    public async Task UnknownQualificationRejectsTrackedBulkAndPreparationAtomically()
    {
        var owner = await fixture.CreateUserAsync();
        await using var seed = fixture.CreateContext(); var row = Row(owner.Id); seed.Add(row); await seed.SaveChangesAsync();
        var preparation = new LocationHistoryPreparation(seed);
        await preparation.BeginAsync("text", true, default);
        while (!await preparation.PrepareBatchAsync("text", default)) { }
        var rows = await RowsAsync(seed); var states = await StatesAsync(seed);
        using var services = new ServiceCollection().AddSingleton(HistoryDerivationEnvironment.Current with { Parser = "unknown" }).BuildServiceProvider();
        await using var unknown = Context(services);
        var rejected = new LocationHistoryPreparation(unknown);
        Assert.False((await rejected.StatusAsync("text", default)).Qualified);
        Assert.False((await rejected.StatusAsync("text", default)).Ready);
        await Assert.ThrowsAsync<HistoryQualificationException>(() => rejected.IsReadyAsync(false, true, default));
        await Assert.ThrowsAsync<HistoryQualificationException>(() => rejected.BeginAsync("text", true, default));
        await Assert.ThrowsAsync<HistoryQualificationException>(() => rejected.PrepareBatchAsync("text", default));
        var edit = await unknown.Locations.SingleAsync(item => item.Id == row.Id); edit.Notes = "rejected";
        await Assert.ThrowsAsync<HistoryQualificationException>(() => unknown.SaveChangesAsync());
        unknown.ChangeTracker.Clear(); unknown.Add(Row(owner.Id));
        Assert.Throws<HistoryQualificationException>(() => unknown.SaveChanges());
        unknown.ChangeTracker.Clear();
        await using (var transaction = await unknown.Database.BeginTransactionAsync())
        {
            await unknown.Locations.Where(item => item.Id == row.Id).ExecuteUpdateAsync(setters => setters.SetProperty(item => item.Address, "rejected bulk"));
            var final = await unknown.Locations.AsNoTracking().SingleAsync(item => item.Id == row.Id);
            await Assert.ThrowsAsync<HistoryQualificationException>(() => LocationHistoryStorage.UpdateAsync(unknown, final, false, true, default));
            await transaction.RollbackAsync();
        }
        Assert.Equal(rows, await RowsAsync(seed)); Assert.Equal(states, await StatesAsync(seed));
    }

    /// <summary>Qualification gates the dependent predicates without taking down startup, UTC lists or owner detail.</summary>
    [PostgresTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RoutedQualificationFailuresRemainDistinctFromPreparation(bool time)
    {
        var owner = await fixture.CreateUserAsync();
        await using var seed = fixture.CreateContext(); var row = Row(owner.Id); seed.Add(row);
        seed.Add(new ApiToken { User = null!, UserId = owner.Id, Name = "history qualification", TokenHash = ApiTokenService.HashToken(owner.Id) });
        await seed.SaveChangesAsync();
        var preparation = new LocationHistoryPreparation(seed);
        foreach (var kind in new[] { "time", "text" })
        {
            await preparation.BeginAsync(kind, true, default);
            while (!await preparation.PrepareBatchAsync(kind, default)) { }
        }
        var current = HistoryDerivationEnvironment.Current;
        using var environmentServices = new ServiceCollection().AddSingleton(time ? current with { Tzdb = "unknown" } : current with { Parser = "unknown" }).BuildServiceProvider();
        await using var app = await IdentityRouteHost.StartAsync(seed, CreateTestDirectory(), services =>
        {
            services.RemoveAll<ApplicationDbContext>();
            services.AddScoped(_ => Context(environmentServices));
        }, host => host.UseMiddleware<ApiErrorResponseMiddleware>());
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", owner.Id);
        const string root = "/api/location/history";
        const string utc = "timeBasis=utc&timeMode=interval&viewerZone=UTC&startUtc=1925-01-01T00:00:00Z&endUtc=1926-01-01T00:00:00Z";
        var affected = time ? "timeBasis=capture&timeMode=days&viewerZone=UTC&fromDate=1925-01-01&throughDate=1925-12-31" : utc + "&q=café";
        foreach (var route in new[] { root + "?", root + "/overview?", root + "?resultSet=unresolvedCandidates&" })
        {
            var rejected = await client.GetAsync(route + affected);
            Assert.Equal(503, (int)rejected.StatusCode);
            using var failure = JsonDocument.Parse(await rejected.Content.ReadAsStringAsync());
            Assert.Equal("historyQualificationRequired", failure.RootElement.GetProperty("code").GetString());
        }
        Assert.Equal(200, (int)(await client.GetAsync(root + "?" + utc)).StatusCode);
        var response = await client.GetAsync(root + "/" + row.Id + "?viewerZone=UTC");
        Assert.Equal(200, (int)response.StatusCode);
        using var detail = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var versions = detail.RootElement.GetProperty("derivationVersions");
        Assert.Equal(JsonValueKind.Null, versions.GetProperty(time ? "time" : "text").ValueKind);
        var capture = detail.RootElement.GetProperty("record").GetProperty("captureTime");
        Assert.Equal(time ? "unavailable" : "available", capture.GetProperty("status").GetString());
        if (time) Assert.Equal("historyQualificationRequired", capture.GetProperty("reason").GetString());
    }

    /// <summary>Each context owns its environment; tests never mutate process-wide serving rules.</summary>
    private ApplicationDbContext Context(IServiceProvider services) => new(new DbContextOptionsBuilder<ApplicationDbContext>()
        .UseNpgsql(fixture.ConnectionString, provider => provider.UseNetTopologySuite()).Options, services);

    /// <summary>Byte-equivalent database snapshots include every canonical, derived and provenance column.</summary>
    private static Task<string[]> RowsAsync(ApplicationDbContext db) => db.Database
        .SqlQueryRaw<string>("SELECT row_to_json(l)::text AS \"Value\" FROM \"Locations\" l ORDER BY \"Id\"").ToArrayAsync();

    /// <summary>Includes timestamps, active/target identities and all committed progress counters.</summary>
    private static Task<string[]> StatesAsync(ApplicationDbContext db) => db.Database
        .SqlQueryRaw<string>("SELECT row_to_json(s)::text AS \"Value\" FROM \"HistoryPreparationStates\" s ORDER BY \"Kind\"").ToArrayAsync();

    /// <summary>R1's two measured counterexamples alongside retained imported evidence.</summary>
    private static Location Row(string owner) => new()
    {
        UserId = owner, IdempotencyKey = Guid.NewGuid(), Coordinates = new Point(-6.2603, 53.3498) { SRID = 4326 },
        TimeZoneId = "retained-import-zone", Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        LocalTimestamp = new DateTime(1925, 9, 25, 12, 0, 0, DateTimeKind.Utc), Address = "Café road", Source = "import",
        Notes = "<math><annotation-xml encoding='TEXT/HTML'><style><p>secret</p></style></annotation-xml></math><p>visible</p>"
    };
}
