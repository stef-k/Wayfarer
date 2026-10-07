using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Controllers;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Real canonical-token constraints, conditional updates, commits and subsequent bearer requests.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ConnectionTokenPostgresTests(PostgresImportTestFixture fixture) : TestBase
{
    /// <summary>Competing missing-row creates cross the unique index with one secret and one conflict.</summary>
    [PostgresFact]
    public async Task CompetingCreates_HaveOneCommittedSecret()
    {
        var user = await fixture.CreateUserAsync();
        var barrier = new CreateBarrier();
        await using var first = fixture.CreateContext(barrier);
        await using var second = fixture.CreateContext(barrier);
        var results = await Task.WhenAll(Service(first).CreateConnectionTokenAsync(user.Id),
            Service(second).CreateConnectionTokenAsync(user.Id));
        var winner = Assert.Single(results, result => result != null)!;
        Assert.Single(results, result => result == null);
        await using var fresh = fixture.CreateContext();
        var row = Assert.Single(await fresh.ApiTokens.Where(row => row.UserId == user.Id).ToListAsync());
        Assert.Null(row.Token);
        Assert.Equal(ApiTokenService.HashToken(winner.Token), row.TokenHash);
        Assert.Equal(winner.IssuedAt, row.CreatedAt);
    }

    /// <summary>Only one update can match an observed issuance; other named and other-owner verifiers survive unchanged.</summary>
    [PostgresFact]
    public async Task CompetingReplacements_RejectStaleStateAndPreserveOtherCredentials()
    {
        var user = await fixture.CreateUserAsync();
        var other = await fixture.CreateUserAsync();
        await using var setup = fixture.CreateContext();
        var initial = (await Service(setup).CreateConnectionTokenAsync(user.Id))!;
        var foreign = (await Service(setup).CreateConnectionTokenAsync(other.Id))!;
        setup.ApiTokens.Add(new ApiToken { UserId = user.Id, User = await setup.Users.SingleAsync(row => row.Id == user.Id),
            Name = "administrative-extra", Token = "legacy-extra", TokenHash = ApiTokenService.HashToken("extra") });
        await setup.SaveChangesAsync();
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var results = await Task.WhenAll(Service(first).ReplaceConnectionTokenAsync(user.Id, initial.TokenId, initial.IssuedAt),
            Service(second).ReplaceConnectionTokenAsync(user.Id, initial.TokenId, initial.IssuedAt));
        var winner = Assert.Single(results, result => result != null)!;
        Assert.Single(results, result => result == null);
        Assert.True(winner.IssuedAt > initial.IssuedAt);
        await using var fresh = fixture.CreateContext();
        var row = await fresh.ApiTokens.AsNoTracking().SingleAsync(row => row.Id == winner.TokenId);
        Assert.Null(row.Token);
        Assert.Equal(ApiTokenService.HashToken(winner.Token), row.TokenHash);
        Assert.Equal(0, row.CreatedAt.Ticks % 10);
        var extra = await fresh.ApiTokens.AsNoTracking().SingleAsync(row => row.UserId == user.Id && row.Name == "administrative-extra");
        Assert.Equal("legacy-extra", extra.Token);
        Assert.Equal(ApiTokenService.HashToken("extra"), extra.TokenHash);
        Assert.Null(await Service(fresh).ReplaceConnectionTokenAsync(user.Id, foreign.TokenId, foreign.IssuedAt));
        Assert.Null(await Service(fresh).ReplaceConnectionTokenAsync(user.Id, extra.Id, extra.CreatedAt));
        Assert.Null(await new IncomingApiTokenResolver(fresh, ApiWorkAdmission.TokenLookups).ResolveTokenAsync(initial.Token, "old-request"));
        Assert.Equal(user.Id, (await new IncomingApiTokenResolver(fresh, ApiWorkAdmission.TokenLookups).ResolveTokenAsync(winner.Token, "new-request"))?.Id);
        Assert.Equal(other.Id, (await new IncomingApiTokenResolver(fresh, ApiWorkAdmission.TokenLookups).ResolveTokenAsync(foreign.Token, "other-request"))?.Id);
    }

    /// <summary>Timestamp advancement remains strict even if the existing issuance is ahead of the current clock.</summary>
    [PostgresFact]
    public async Task ReplacementClearsLegacyPlaintextAndAdvancesAtMicrosecondPrecision()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var initial = (await Service(db).CreateConnectionTokenAsync(user.Id))!;
        var future = new DateTime(2090, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        await db.ApiTokens.Where(row => row.Id == initial.TokenId).ExecuteUpdateAsync(setters =>
            setters.SetProperty(row => row.CreatedAt, future).SetProperty(row => row.Token, "legacy"));
        var result = (await Service(db).ReplaceConnectionTokenAsync(user.Id, initial.TokenId, future))!;
        Assert.Equal(future.AddTicks(10), result.IssuedAt);
        await using var fresh = fixture.CreateContext();
        var row = await fresh.ApiTokens.SingleAsync(row => row.Id == result.TokenId);
        Assert.Null(row.Token);
        Assert.Equal(result.IssuedAt, row.CreatedAt);
    }

    /// <summary>Failure before commit rolls back both Create and Replace and emits no issue result.</summary>
    [PostgresTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CommitFailure_RollsBackWithoutSecret(bool replace)
    {
        var user = await fixture.CreateUserAsync();
        await using var setup = fixture.CreateContext();
        var initial = replace ? await Service(setup).CreateConnectionTokenAsync(user.Id) : null;
        await using var failing = fixture.CreateContext(new CommitFailure());
        await Assert.ThrowsAsync<DbUpdateException>(async () =>
        {
            if (replace) await Service(failing).ReplaceConnectionTokenAsync(user.Id, initial!.TokenId, initial.IssuedAt);
            else await Service(failing).CreateConnectionTokenAsync(user.Id);
        });
        await using var fresh = fixture.CreateContext();
        var row = await fresh.ApiTokens.SingleOrDefaultAsync(row => row.UserId == user.Id);
        if (replace) Assert.Equal(ApiTokenService.HashToken(initial!.Token), row?.TokenHash);
        else Assert.Null(row);
    }

    /// <summary>A caller-owned uncommitted transaction cannot receive a potentially uncommitted plaintext.</summary>
    [PostgresFact]
    public async Task CallerTransactionCannotReceiveSecret()
    {
        var user = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(db).CreateConnectionTokenAsync(user.Id));
        Assert.False(await db.ApiTokens.AnyAsync(row => row.UserId == user.Id));
    }

    /// <summary>Production HTTP maps stale/foreign/missing rows to secret-free 409 and committed replacement to 200.</summary>
    [PostgresFact]
    public async Task RoutedReplacement_EnforcesOwnerPreconditionAndReturnsConflict()
    {
        var user = await fixture.CreateUserAsync();
        var foreignUser = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var initial = (await Service(db).CreateConnectionTokenAsync(user.Id))!;
        var foreign = (await Service(db).CreateConnectionTokenAsync(foreignUser.Id))!;
        await using var app = await IdentityRouteHost.StartAsync(db, CreateTestDirectory());
        using var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://wayfarer.test");
        UserConnectionTokenHttpTests.Cookie(client, app.Services, user.Id);
        await UserConnectionTokenHttpTests.AddAntiforgeryAsync(client);
        using var forbidden = await client.PostAsJsonAsync("/User/ApiToken/Replace", new
            { tokenId = foreign.TokenId, issuedAt = foreign.IssuedAt, confirmed = true, userId = foreignUser.Id });
        Assert.Equal(HttpStatusCode.Conflict, forbidden.StatusCode);
        var input = new { tokenId = initial.TokenId, issuedAt = initial.IssuedAt, confirmed = true };
        using var success = await client.PostAsJsonAsync("/User/ApiToken/Replace", input);
        Assert.Equal(HttpStatusCode.OK, success.StatusCode);
        var issue = (await success.Content.ReadFromJsonAsync<ConnectionTokenIssue>())!;
        using var conflict = await client.PostAsJsonAsync("/User/ApiToken/Replace", input);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        Assert.DoesNotContain(issue.Token, await conflict.Content.ReadAsStringAsync());
        Assert.True(success.Headers.CacheControl?.NoStore);
        Assert.True(conflict.Headers.CacheControl?.NoStore);
        Assert.False(success.Headers.Contains("Set-Cookie"));
        Assert.Null(success.Headers.Location);
    }

    /// <summary>The guarded operations need no second Identity/token-generation owner.</summary>
    private static ApiTokenService Service(ApplicationDbContext db) => new(db, null!);

    /// <summary>Both missing-row creates reach persistence before either can win the production unique index.</summary>
    private sealed class CreateBarrier : SaveChangesInterceptor
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _ready.SetResult();
            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }

    /// <summary>Fails at the real transaction boundary, after SQL but before PostgreSQL commit.</summary>
    private sealed class CommitFailure : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default) =>
            throw new DbUpdateException("Injected commit failure.");
    }
}
