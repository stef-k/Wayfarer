using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using static Wayfarer.Tests.Controllers.TripMetadataReceiptHttp;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Checks the actual Web adapter after request-entry auth and before final account lock.</summary>
    [PostgresTheory]
    [InlineData("issue")]
    [InlineData("apply")]
    [InlineData("replay")]
    [InlineData("lookup")]
    public async Task Receipt_RevokedCookieCannotCompleteProtectedRequest(string action)
    {
        var trip = await SeedAsync();
        var gate = new ReviewAccountGate();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [gate]);
        using var web = await WebAsync(app, trip.UserId);
        var body = Body(new { name = trip.Name }, new { name = "Submitted name" });
        var permit = await PermitAsync(web, trip, body, true);
        var hasHistory = action is "lookup" or "replay";
        if (hasHistory)
        {
            using var applied = await SendAsync(web, trip, permit, body, true);
            await EvidenceAsync(applied, permit, "applied");
            await db.Trips.Where(row => row.Id == trip.Id).ExecuteDeleteAsync();
        }

        _warmup.Invocations.Clear();
        _thumbnails.Invocations.Clear();
        gate.Armed = true;
        var pending = action switch
        {
            "issue" => IssueAsync(web, trip, body, true),
            "lookup" => LookupAsync(web, trip, permit, true),
            _ => SendAsync(web, trip, permit, body, true)
        };
        try
        {
            await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(8));
            var stamp = Guid.NewGuid().ToString("N");
            Assert.Equal(1, await db.Users.Where(row => row.Id == trip.UserId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.SecurityStamp, stamp)));
        }
        finally { gate.Resume.TrySetResult(); }

        using var inFlight = await pending;
        Assert.Equal(HttpStatusCode.Unauthorized, inFlight.StatusCode);
        var payload = await inFlight.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("mutation-authority-unavailable", payload.GetProperty("code").GetString());
        Assert.False(payload.TryGetProperty("operationId", out _));
        Assert.False(payload.TryGetProperty("historicalOutcome", out _));
        Assert.False(payload.TryGetProperty("proof", out _));
        using var retry = await LookupAsync(web, trip, permit, true);
        Assert.Equal(HttpStatusCode.Unauthorized, retry.StatusCode);

        var stored = await db.Trips.AsNoTracking().SingleOrDefaultAsync(row => row.Id == trip.Id);
        if (hasHistory)
        {
            Assert.Null(stored);
            using var current = await WebAsync(app, trip.UserId);
            using var recovery = await LookupAsync(current, trip, permit, true);
            await EvidenceAsync(recovery, permit, "applied");
        }
        else
        {
            Assert.NotNull(stored);
            Assert.Equal((trip.Name, trip.UpdatedAt), (stored.Name, stored.UpdatedAt));
        }
        var receipts = await db.Database.SqlQuery<long>($"""
            SELECT count(*) AS "Value" FROM public.trip_metadata_receipts WHERE subject_id = {trip.UserId}
            """).SingleAsync();
        Assert.Equal(hasHistory ? 1 : 0, receipts);
        Assert.Empty(_warmup.Invocations);
        Assert.Empty(_thumbnails.Invocations);
    }

    /// <summary>Pauses only the final shared account lock statement, after real request authentication.</summary>
    private sealed class ReviewAccountGate : DbCommandInterceptor
    {
        internal bool Armed { get; set; }
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Lets the test replace the stamp before the locked account row is read.</summary>
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("AspNetUsers") && command.CommandText.Contains("FOR SHARE"))
            {
                Armed = false;
                Reached.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }
}
