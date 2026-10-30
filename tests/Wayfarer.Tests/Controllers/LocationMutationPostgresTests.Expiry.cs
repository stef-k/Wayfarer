using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services.LocationMutations;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>A6/A7/A12 expiry, retention and rollback at existing real transaction seams without waiting for time to pass.</summary>
public sealed partial class LocationMutationPostgresTests
{
    /// <summary>No-row pre-expiry allows arrival; expired original authority excludes arrival without inventing historical outcome.</summary>
    [PostgresFact]
    public async Task LateArrivalAndCleanedHistoryUseSignedExpiryWithoutExpiringWork()
    {
        var (owner, credential, id) = await SeedAsync();
        await WithHostAsync(credential, async client =>
        {
            var request = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "set", html = "late" } });
            var absent = await AssertStateAsync(await request.RecoverAsync(client), 200, "unknown", "operationNotObserved");
            Assert.True(absent.GetProperty("futureEffects").GetBoolean()); Assert.False(absent.TryGetProperty("fingerprint", out _));
            await AssertStateAsync(await request.SendAsync(client), 200, "applied");
            // Synthetic signed authority represents a legitimately old retained handle; clients never edit these fields.
            var proof = LocationMutationBase.Read(protection, request.Handle, owner, id)!;
            var old = proof with { BaseId = Guid.NewGuid(), IssuedAtUtc = DateTime.UtcNow.AddDays(-31), ExpiresAtUtc = DateTime.UtcNow.AddDays(-31).AddMinutes(15) };
            var oldHandle = old.Protect(protection);
            var oldRequest = request with { BaseId = old.BaseId, Handle = oldHandle,
                Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(request.Bytes).Replace(request.Handle, oldHandle)) };
            var expired = await AssertStateAsync(await oldRequest.RecoverAsync(client), 200, "unknown", "operationHistoryExpired");
            Assert.False(expired.GetProperty("futureEffects").GetBoolean()); Assert.Equal("unknown", expired.GetProperty("historicalOutcome").GetString());
            using var refused = await oldRequest.SendAsync(client); Assert.Equal(410, (int)refused.StatusCode);
            await using (var db = fixture.CreateContext())
            {
                // Retained terminal evidence wins even beyond cleanup eligibility, until actually deleted.
                var retained = await db.LocationMutationOperations.SingleAsync(row => row.UserId == owner);
                retained.ExpiresAtUtc = old.ExpiresAtUtc; retained.ReceiptRetainUntilUtc = DateTime.UtcNow.AddSeconds(-1);
                await db.SaveChangesAsync();
            }
            await AssertStateAsync(await request.RecoverAsync(client), 200, "applied");
            await using (var cleanup = fixture.CreateContext()) Assert.Equal(1, (await new LocationMutationCleanup(cleanup).RunAsync(default)).Deleted);
            var unknown = await AssertStateAsync(await oldRequest.RecoverAsync(client), 200, "unknown", "operationHistoryExpired");
            Assert.False(unknown.GetProperty("futureEffects").GetBoolean());
            // Reusing an old base ID with a renewed signed envelope cannot grant execution.
            var renewed = old with { BaseId = Guid.NewGuid(), IssuedAtUtc = DateTime.UtcNow, ExpiresAtUtc = DateTime.UtcNow.AddMinutes(15) };
            var changedHandle = oldRequest with { Bytes = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(oldRequest.Bytes).Replace(oldHandle, renewed.Protect(protection))) };
            using var mismatch = await changedHandle.SendAsync(client); Assert.Equal(422, (int)mismatch.StatusCode);
        });
    }

    /// <summary>GET and exact POST project the same deadline rejection; no executor takeover or terminal write on GET.</summary>
    [PostgresFact]
    public async Task ExpiredPendingProjectsFixedTerminalityAndCleanupRetainsFreshRows()
    {
        var (owner, credential, id) = await SeedAsync();
        await WithHostAsync(credential, async client =>
        {
            var request = await ProtectedLocationRequest.CreateAsync(client, id, new { notes = new { op = "clear" } });
            var deadline = new DateTime(DateTime.UtcNow.AddSeconds(-1).Ticks / 10 * 10, DateTimeKind.Utc);
            await using (var db = fixture.CreateContext())
            {
                db.LocationMutationOperations.Add(new() { UserId = owner, LocationId = id, Uuid = (await db.Locations.SingleAsync(row => row.Id == id)).IdempotencyKey,
                    BaseId = request.BaseId, ActionId = request.ActionId, Adapter = "owner", Kind = "update", Normalization = "location-values-1",
                    Fingerprint = request.Fingerprint(owner), ExpiresAtUtc = deadline.AddSeconds(10), AcceptedAtUtc = deadline.AddSeconds(-120),
                    ExecuteBeforeUtc = deadline, ReceiptRetainUntilUtc = deadline.AddDays(30), ExecutorNonce = Guid.NewGuid(), State = "pending" });
                await db.SaveChangesAsync();
            }
            var get = await AssertStateAsync(await request.RecoverAsync(client), 409, "rejected", "operationExecutionExpired");
            var post = await AssertStateAsync(await request.SendAsync(client), 409, "rejected", "operationExecutionExpired");
            Assert.Equal(get.GetRawText(), post.GetRawText());
            await using var verify = fixture.CreateContext();
            Assert.Null((await verify.LocationMutationOperations.SingleAsync(row => row.UserId == owner)).ResultJson);
            Assert.Equal(0, (await new LocationMutationCleanup(verify).RunAsync(default)).Deleted);
            Assert.Equal("original", (await verify.Locations.SingleAsync(row => row.Id == id)).Notes);
        });
    }

    /// <summary>Deletion effects, marker, dependent removal and receipt all disappear when the actual commit fails.</summary>
    [PostgresFact]
    public async Task DeletionRollbackCannotLeaveMarkerOrReceipt()
    {
        var (owner, credential, id) = await SeedAsync();
        var fault = new BeforeCommit();
        await WithHostAsync(credential, async client =>
        {
            var request = await ProtectedLocationRequest.CreateAsync(client, id, null);
            fault.Armed = true;
            using var response = await request.SendAsync(client); Assert.True((int)response.StatusCode >= 500);
            await using var verify = fixture.CreateContext();
            Assert.True(await verify.Locations.AnyAsync(row => row.Id == id));
            Assert.False(await verify.Set<LocationDeletionMarker>().AnyAsync(row => row.UserId == owner));
            Assert.False(await verify.LocationMutationOperations.AnyAsync(row => row.UserId == owner));
            var observation = await AssertStateAsync(await request.RecoverAsync(client), 200, "unknown", "operationNotObserved");
            Assert.True(observation.GetProperty("futureEffects").GetBoolean());
            await AssertStateAsync(await request.SendAsync(client), 200, "applied");
        }, fault);
    }

    /// <summary>Fails only the next real commit, leaving PostgreSQL rollback to own every effect.</summary>
    private sealed class BeforeCommit : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        internal bool Armed;
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData data,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return ValueTask.FromResult(result);
            Armed = false; throw new IOException("Controlled precommit failure.");
        }
    }
}
