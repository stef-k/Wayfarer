using Microsoft.AspNetCore.Http;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Wayfarer.Util;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Shared token matching, request ownership, and lookup admission at the database seam.</summary>
public class IncomingApiTokenResolverTests : TestBase
{
    /// <summary>Hash/plaintext matching, provider exclusion, missing user and account activity stay unified.</summary>
    [Theory]
    [InlineData(true, "phone", true, true, true)]
    [InlineData(false, "phone", true, true, true)]
    [InlineData(true, " MAPbox ", true, true, false)]
    [InlineData(false, " MAPbox ", true, true, false)]
    [InlineData(true, "phone", false, true, false)]
    [InlineData(true, "phone", true, false, false)]
    public async Task Matching(bool hashed, string name, bool active, bool userExists, bool expected)
    {
        using var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        user.IsActive = active;
        if (userExists) db.Users.Add(user);
        db.ApiTokens.Add(new ApiToken { User = null!, UserId = user.Id, Name = name,
            Token = hashed ? null : "secret", TokenHash = hashed ? ApiTokenService.HashToken("secret") : null });
        await db.SaveChangesAsync();
        var resolver = new IncomingApiTokenResolver(db, new ApiWorkAdmission(32, 16));
        Assert.Equal(expected, await resolver.ResolveTokenAsync("secret", "ip") != null);
        Assert.Null(await resolver.ResolveTokenAsync("revoked", "ip"));
        Assert.Null(await resolver.ResolveTokenAsync(null, "ip"));
    }

    /// <summary>Legacy loose parsing and distinct named device tokens resolve the same active account.</summary>
    [Fact]
    public async Task RequestOwnershipAndNamedTokens()
    {
        using var db = CreateDbContext();
        var user = TestDataFixtures.CreateUser(id: "u1");
        db.Users.Add(user);
        db.ApiTokens.AddRange(new ApiToken { User = null!, UserId = user.Id, Name = "phone", Token = "one" },
            new ApiToken { User = null!, UserId = user.Id, Name = "tablet", TokenHash = ApiTokenService.HashToken("two") });
        await db.SaveChangesAsync();
        foreach (var header in new[] { "one", "NotBearer ignored one", "Bearer two" })
        {
            var context = new DefaultHttpContext();
            context.Request.Headers.Authorization = header;
            var resolver = IncomingApiTokenResolver.ForRequest(context, db);
            Assert.Same(resolver, IncomingApiTokenResolver.ForRequest(context, db));
            Assert.Equal(user.Id, (await resolver.ResolveAsync(context))?.Id);
        }
        db.ApiTokens.RemoveRange(db.ApiTokens);
        await db.SaveChangesAsync();
        Assert.Null(await new IncomingApiTokenResolver(db, new ApiWorkAdmission(32, 16)).ResolveTokenAsync("one", "ip"));
    }

    /// <summary>Lookup admission rejects immediately, before entering hashing/database work.</summary>
    [Theory]
    [InlineData(32, false, 503)]
    [InlineData(16, true, 429)]
    public async Task GatedLookupSaturation(int count, bool sameIp, int expected)
    {
        using var db = CreateDbContext();
        var owner = new ApiWorkAdmission(32, 16);
        var release = new TaskCompletionSource<ApplicationUser?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new GatedResolver(db, owner, release.Task);
        var requests = Enumerable.Range(0, count)
            .Select(i => resolver.ResolveTokenAsync($"random-{i}", sameIp ? "ip" : i.ToString())).ToArray();
        Assert.Equal(count, resolver.Entries);
        await Assert.ThrowsAsync<ApiTokenAdmissionException>(() => resolver.ResolveTokenAsync("denied", sameIp ? "ip" : "extra"));
        Assert.Equal(expected, resolver.DeniedStatus);
        Assert.Equal(count, resolver.Entries);
        release.SetResult(null);
        await Task.WhenAll(requests);
        Assert.Equal(0, owner.IdentityCount);
    }

    /// <summary>Success, invalid tokens, store failures and cancellation all release their permits.</summary>
    [Theory]
    [InlineData("success")]
    [InlineData("invalid")]
    [InlineData("failure")]
    [InlineData("cancel")]
    public async Task LookupAlwaysReleases(string outcome)
    {
        using var db = CreateDbContext();
        var owner = new ApiWorkAdmission(32, 16);
        var completion = new TaskCompletionSource<ApplicationUser?>();
        if (outcome == "failure") completion.SetException(new InvalidOperationException());
        else if (outcome == "cancel") completion.SetCanceled();
        else completion.SetResult(outcome == "success" ? TestDataFixtures.CreateUser() : null);
        var resolver = new GatedResolver(db, owner, completion.Task);
        var invoke = () => resolver.ResolveTokenAsync("token", "ip");
        if (outcome == "failure") await Assert.ThrowsAsync<InvalidOperationException>(invoke);
        else if (outcome == "cancel") await Assert.ThrowsAnyAsync<OperationCanceledException>(invoke);
        else Assert.Equal(outcome == "success", await invoke() != null);
        Assert.Equal(0, owner.IdentityCount);
    }

    /// <summary>Holds the production lookup boundary without timers or live database flooding.</summary>
    private sealed class GatedResolver(ApplicationDbContext db, ApiWorkAdmission owner, Task<ApplicationUser?> completion)
        : IncomingApiTokenResolver(db, owner)
    {
        public int Entries { get; private set; }
        protected override Task<ApplicationUser?> LookupAsync(string token, CancellationToken cancellationToken)
        {
            Entries++;
            return completion;
        }
    }
}
