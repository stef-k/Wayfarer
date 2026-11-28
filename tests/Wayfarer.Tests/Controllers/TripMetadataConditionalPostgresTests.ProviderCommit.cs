using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NetTopologySuite.Geometries;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Services.TripMetadataReceipts;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Mapbox first-use charges hold the late cookie lock through commit. Rotation still refuses final apply.</summary>
    [PostgresFact]
    public async Task StructureReceipt_MeterCommitHoldsCookieLockBeforeCombinedSave()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        var region = await SeedStructureRegionAsync(db, trip);
        var place = new Place { Id = Guid.NewGuid(), UserId = trip.UserId, RegionId = region.Id, Name = "Frozen Place",
            Address = "Original address", Location = new Point(23.72, 37.98) { SRID = 4326 } };
        db.Places.Add(place);
        await db.SaveChangesAsync();
        var freshness = await db.Trips.Where(row => row.Id == trip.Id).Select(row => row.UpdatedAt).SingleAsync();
        var credentials = CredentialTestFactory.Create(new EphemeralDataProtectionProvider());
        await SeedProtectedProviderAsync(db, trip.UserId, credentials, PersonalLocationProvider.Mapbox);
        var contact = new CountProviderContact();
        var gate = new ProviderChargeCommitGate();
        var commands = new MeterCommitCommands(gate);
        await using var app = await StartProtectedProviderHostAsync(db, credentials, contact, commands, gate);
        using var web = await WebAsync(app, trip.UserId);
        var (route, body, permit) = await PrepareMeteredPlaceSaveAsync(web, trip, place);

        var pending = TripMetadataReceiptHttp.SendAsync(web, HttpMethod.Put, route, permit, body);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await using var rotation = fixture.CreateContext();
        try
        {
            var holder = await gate.BeforeCommit.Task.WaitAsync(timeout.Token);
            await rotation.Database.OpenConnectionAsync(timeout.Token);
            var waiter = ((NpgsqlConnection)rotation.Database.GetDbConnection()).ProcessID;
            var stamp = Guid.NewGuid().ToString("N");
            var rotate = rotation.Users.Where(user => user.Id == trip.UserId)
                .ExecuteUpdateAsync(set => set.SetProperty(user => user.SecurityStamp, stamp), timeout.Token);
            await ObserveProviderBlockAsync(holder, waiter, timeout.Token);
            Assert.False(rotate.IsCompleted);
            gate.ReleaseCommit.TrySetResult();
            await gate.BeforeContact.Task.WaitAsync(timeout.Token);
            Assert.Equal(1, await rotate);
            Assert.Equal(1, await db.MapboxProductMeters.Where(meter => meter.UserId == trip.UserId
                && meter.Product == PersonalProviderProduct.PermanentGeocoding).Select(meter => meter.AdmittedCount).SingleAsync(timeout.Token));
            output.WriteLine($"Stamp rotation backend {waiter} waited on the Mapbox charge commit backend {holder}.");
        }
        finally
        {
            gate.ReleaseCommit.TrySetResult();
            gate.ReleaseContact.TrySetResult();
        }

        using var reply = await pending.WaitAsync(timeout.Token);
        Assert.Equal(HttpStatusCode.Unauthorized, reply.StatusCode);
        var problem = await reply.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mutation-authority-unavailable", problem.GetProperty("code").GetString());
        Assert.False(problem.TryGetProperty("operationId", out _));
        Assert.InRange(contact.Calls, 0, 1);
        Assert.Equal("Original address", await db.Places.Where(row => row.Id == place.Id).Select(row => row.Address).SingleAsync());
        Assert.Equal(freshness, await db.Trips.Where(row => row.Id == trip.Id).Select(row => row.UpdatedAt).SingleAsync());
    }

    /// <summary>Obtains real reviewed structural consent and freezes the same request in a typed permit.</summary>
    private static async Task<(string Route, string Body, JsonElement Permit)> PrepareMeteredPlaceSaveAsync(HttpClient web, Trip trip, Place place)
    {
        var path = $"/api/trips/{trip.Id}/editor/places/{place.Id}";
        var expected = new { regionId = place.RegionId, address = place.Address, location = new { latitude = 37.98, longitude = 23.72 } };
        var changes = new { reverseGeocode = true };
        using var prepared = await web.PostAsJsonAsync(path + "/prepare", new { expected, changes });
        Assert.Equal(HttpStatusCode.OK, prepared.StatusCode);
        var conditions = await prepared.Content.ReadFromJsonAsync<JsonElement>();
        var body = JsonSerializer.Serialize(new { receiptContract = TripMetadataIdentity.StructureVersion, expected, changes,
            conditionToken = conditions.GetProperty("conditionToken").GetString(), reviewed = true });
        var route = path + "/receipt";
        var identity = new TripMetadataIdentity(TripMetadataIdentity.StructureVersion, trip.UserId, "web", "place-save",
            trip.Id, "", Guid.Empty, default) { Target = new("place", place.Id) };
        return (route, body, await TripMetadataReceiptHttp.IssueTypedAsync(web, identity, route, body));
    }

    /// <summary>Gates only the original metered commit, after the account check and staging of the charge.</summary>
    private sealed class ProviderChargeCommitGate : DbTransactionInterceptor
    {
        internal DbTransaction? Admission { get; set; }
        internal TaskCompletionSource<int> BeforeCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseCommit { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource BeforeContact { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseContact { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken token = default)
        {
            if (transaction == Admission)
            {
                BeforeCommit.TrySetResult(((NpgsqlConnection)transaction.Connection!).ProcessID);
                await ReleaseCommit.Task.WaitAsync(token);
            }
            return result;
        }
    }

    /// <summary>Identifies the metered transaction and the subsequent provider-authority read without an open transaction.</summary>
    private sealed class MeterCommitCommands(ProviderChargeCommitGate gate) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken token = default)
        {
            if (command.CommandText.Contains("INSERT INTO \"MapboxProductMeters\"", StringComparison.Ordinal))
                gate.Admission = command.Transaction;
            return ValueTask.FromResult(result);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken token = default)
        {
            if (gate.ReleaseCommit.Task.IsCompleted && command.Transaction == null
                && command.CommandText.Contains("PersonalLocationProviderSelections", StringComparison.Ordinal) && gate.BeforeContact.TrySetResult())
                await gate.ReleaseContact.Task.WaitAsync(token);
            return result;
        }
    }
}
