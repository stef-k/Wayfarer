using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Services.TripMetadataReceipts;
using Wayfarer.Util;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using static Wayfarer.Tests.Controllers.TripMetadataReceiptHttp;

namespace Wayfarer.Tests.Controllers;

public sealed partial class TripMetadataConditionalPostgresTests
{
    /// <summary>Both adapters recover exactly once after a response is dropped following the authoritative commit.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Receipt_LostReply_ExactReplay_AndNoOp(bool web)
    {
        var trip = await SeedAsync(null);
        var probe = new MutationProbe();
        var dropReply = true;
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [probe], app => app.Use(async (context, next) =>
        {
            if (!dropReply || context.Request.Method != "PATCH") { await next(); return; }
            dropReply = false;
            var transport = context.Response.Body;
            await using var buffer = new MemoryStream();
            context.Response.Body = buffer;
            try { await next(); }
            finally { context.Response.Body = transport; }
            throw new IOException("Controlled lost response after receipt commit");
        }));
        using var client = web ? await WebAsync(app, trip.UserId) : Bearer(app, trip.UserId);
        var field = web ? "notesHtml" : "notes";
        var before = new Dictionary<string, string?> { [field] = null };
        var after = new Dictionary<string, string?> { [field] = "" };
        var body = Body(before, after);
        var permit = await PermitAsync(client, trip, body, web);

        using var lost = await SendAsync(client, trip, permit, body, web);
        Assert.Equal(HttpStatusCode.InternalServerError, lost.StatusCode);
        using var lookup = await LookupAsync(client, trip, permit, web);
        var recovered = await EvidenceAsync(lookup, permit, "applied");
        Assert.Equal("sha256:ade28f8613cc198c662064936122c058973a157ca1713b6985d473596a78cfcc",
            recovered.GetProperty("fieldDigests").GetProperty("notes").GetString());
        Assert.False(recovered.GetProperty("futureEffects").GetBoolean());
        using var replay = await SendAsync(client, trip, permit, body, web);
        Assert.Equal(recovered.GetRawText(), (await EvidenceAsync(replay, permit, "applied")).GetRawText());
        using var changed = await SendAsync(client, trip, permit, body + "\n", web);
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);
        Assert.Equal("trip-attempt-digest-mismatch", (await changed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        var noop = Body(after, after);
        using var noopReply = await SendAsync(client, trip, await PermitAsync(client, trip, noop, web), noop, web);
        Assert.Equal("checkedNoOp", (await noopReply.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("state").GetString());
        Assert.Single(probe.Updates);
        Assert.Single(_warmup.Invocations);
        Assert.Equal(web ? 1 : 0, _thumbnails.Invocations.Count);
        output.WriteLine(Route(trip, web));
        output.WriteLine(body);
        output.WriteLine(recovered.GetRawText());
    }

    /// <summary>Stale originals produce a terminal zero-effect result even when another author later writes the requested value.</summary>
    [PostgresFact]
    public async Task Receipt_StaleOriginalAndCredentialReplacementAfterTargetLoss()
    {
        var trip = await SeedAsync();
        var foreign = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var client = Bearer(app, trip.UserId);
        var body = Body(new { name = "Stale name", notes = trip.Notes }, new { name = "Independent value", notes = "Must not commit" });
        var permit = await PermitAsync(client, trip, body);
        using var refusal = await SendAsync(client, trip, permit, body);
        await EvidenceAsync(refusal, permit, "terminalUnapplied", 409);
        var stored = await db.Trips.AsNoTracking().SingleAsync(row => row.Id == trip.Id);
        Assert.Equal((trip.Name, trip.Notes, trip.UpdatedAt), (stored.Name, stored.Notes, stored.UpdatedAt));
        using var independent = await PatchAsync(client, trip.Id, false, new { name = trip.Name }, new { name = "Independent value" });
        Assert.Equal(HttpStatusCode.OK, independent.StatusCode);
        using var other = Bearer(app, foreign.UserId);
        using var foreignLookup = await LookupAsync(other, trip, permit);
        Assert.Equal(HttpStatusCode.Conflict, foreignLookup.StatusCode);
        Assert.DoesNotContain("historicalOutcome", await foreignLookup.Content.ReadAsStringAsync());
        using var beforeDelete = await LookupAsync(client, trip, permit);
        await EvidenceAsync(beforeDelete, permit, "terminalUnapplied");

        await db.Trips.Where(row => row.Id == trip.Id).ExecuteDeleteAsync();
        await db.ApiTokens.Where(row => row.UserId == trip.UserId).ExecuteDeleteAsync();
        var replacement = Guid.NewGuid().ToString("N");
        db.ApiTokens.Add(new ApiToken { UserId = trip.UserId, User = null!, Name = "Replacement", TokenHash = ApiTokenService.HashToken(replacement) });
        await db.SaveChangesAsync();
        using var revoked = await LookupAsync(client, trip, permit);
        Assert.Equal(HttpStatusCode.Unauthorized, revoked.StatusCode);
        using var newClient = Bearer(app, replacement);
        using var afterDelete = await LookupAsync(newClient, trip, permit);
        await EvidenceAsync(afterDelete, permit, "terminalUnapplied");
    }

    /// <summary>A pre-COMMIT failure leaves neither changed fields nor a successful receipt.</summary>
    [PostgresFact]
    public async Task Receipt_RollbackRemovesStagedChangesAndEvidence()
    {
        var trip = await SeedAsync();
        var commitFailure = new FailSelectedCommit();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db, [commitFailure]);
        using var client = await WebAsync(app, trip.UserId);
        var body = Body(new { name = trip.Name, notesHtml = trip.Notes }, new { name = "Must roll back", notesHtml = "Must also roll back" });
        var permit = await PermitAsync(client, trip, body, true);
        commitFailure.FailNext = true;
        using var failed = await SendAsync(client, trip, permit, body, true);
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);
        using var result = await LookupAsync(client, trip, permit, true);
        var evidence = await EvidenceAsync(result, permit, "unobserved");
        Assert.True(evidence.GetProperty("futureEffects").GetBoolean());
        var stored = await db.Trips.AsNoTracking().SingleAsync(row => row.Id == trip.Id);
        Assert.Equal((trip.Name, trip.Notes, trip.UpdatedAt), (stored.Name, stored.Notes, stored.UpdatedAt));
        Assert.Empty(_warmup.Invocations);
        Assert.Empty(_thumbnails.Invocations);
    }

    /// <summary>The marker and proof select exactly one supported write grammar and never fall back to a mixed patch.</summary>
    [PostgresFact]
    public async Task Receipt_MarkerAndPermitMustMatchTheNarrowGrammar()
    {
        var trip = await SeedAsync();
        await using var db = fixture.CreateContext();
        await using var app = await StartAsync(db);
        using var web = await WebAsync(app, trip.UserId);
        using var noAntiforgery = app.GetTestClient();
        UserConnectionTokenHttpTests.Cookie(noAntiforgery, app.Services, trip.UserId);
        var body = Body(new { name = trip.Name }, new { name = "Never apply" });
        using var forbiddenIssue = await IssueAsync(noAntiforgery, trip, body, true);
        Assert.Equal(HttpStatusCode.BadRequest, forbiddenIssue.StatusCode);
        using var noProof = await web.PatchAsync(Route(trip, true), new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, noProof.StatusCode);

        var ordinary = JsonSerializer.Serialize(new { expected = new { name = trip.Name }, changes = new { name = "Never apply" } });
        var wide = Body(new { name = trip.Name, isPublic = true }, new { name = "Never apply", isPublic = false });
        foreach (var invalid in new[] { ordinary, wide, body.Replace(Contract, "trip-metadata-receipt-2", StringComparison.Ordinal) })
        {
            var permit = await PermitAsync(web, trip, invalid, true);
            using var refused = await SendAsync(web, trip, permit, invalid, true);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.DoesNotContain("historicalOutcome", await refused.Content.ReadAsStringAsync());
        }
        var stored = await db.Trips.AsNoTracking().SingleAsync(row => row.Id == trip.Id);
        Assert.Equal((trip.Name, trip.IsPublic, trip.ShareProgressEnabled, trip.UpdatedAt),
            (stored.Name, stored.IsPublic, stored.ShareProgressEnabled, stored.UpdatedAt));
        Assert.Empty(_warmup.Invocations);
    }

    /// <summary>Pins the two published byte-framing examples and all nullable-result vectors.</summary>
    [Theory]
    [InlineData("bearer", "notes", "sha256:ca8cac3c9a7057d7c2fed234569f2b27c231d07cf48942125521e63ab07b53cd")]
    [InlineData("web", "notesHtml", "sha256:8132ed45591546135ee538d8d4e9eb36a1ff65f6274d60d6dc4ed59b617f9ad4")]
    public void Receipt_PublishedVectors(string adapter, string field, string expected)
    {
        var body = Body(new Dictionary<string, object?> { [field] = null }, new Dictionary<string, object?> { [field] = "" });
        Assert.Equal(expected, TripMetadataIdentity.Digest("owner-a", adapter, Guid.Parse("12345678-1234-4234-8234-123456789abc"), Encoding.UTF8.GetBytes(body)));
        Assert.Equal("sha256:27282ceb8203bfee8a47dd799f6b4373a3b024707320d591f6cf67fbbda922c7",
            TripMetadataIdentity.FieldDigest("notes", null));
        Assert.Equal("sha256:332856ea32981252ff827d0120f9d22f5dd5ac4aef0230dc749f897036ab37c6",
            TripMetadataIdentity.FieldDigest("notes", "<p>Αθήνα</p>"));
    }

    /// <summary>Fails exactly the chosen write commit without interfering with issuance or later observation.</summary>
    private sealed class FailSelectedCommit : Microsoft.EntityFrameworkCore.Diagnostics.DbTransactionInterceptor
    {
        internal bool FailNext { get; set; }

        /// <summary>Injects the failure while the domain and receipt statements are still transactional.</summary>
        public override ValueTask<Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult> TransactionCommittingAsync(
            System.Data.Common.DbTransaction transaction, Microsoft.EntityFrameworkCore.Diagnostics.TransactionEventData data,
            Microsoft.EntityFrameworkCore.Diagnostics.InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!FailNext) return ValueTask.FromResult(result);
            FailNext = false;
            throw new InvalidOperationException("Controlled receipt commit failure");
        }
    }
}
