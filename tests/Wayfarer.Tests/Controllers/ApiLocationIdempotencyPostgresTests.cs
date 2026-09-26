using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Controllers;

/// <summary>Real unique-index arbitration and admission ordering using the established guarded database.</summary>
[Collection(PostgresImportTestCollection.Name)]
public class ApiLocationIdempotencyPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Two first uses converge through save-conflict reread; stored replay ignores saturation.</summary>
    [PostgresFact(Timeout = 30000)]
    public async Task ConcurrentFirstUseAndReplay()
    {
        var user = await fixture.CreateUserAsync();
        await SeedToken(user);
        var key = Guid.NewGuid();
        var gate = new ApiWorkAdmission(64, 8);
        var barrier = new SaveBarrier();
        await using var firstDb = fixture.CreateContext(barrier);
        await using var secondDb = fixture.CreateContext(barrier);
        var first = ApiLocationAdmissionTests.Controller(firstDb, gate);
        var second = ApiLocationAdmissionTests.Controller(secondDb, gate);
        foreach (var controller in new[] { first, second })
        {
            controller.Request.Headers.Authorization = $"Bearer {user.Id}";
            controller.Request.Headers["Idempotency-Key"] = key.ToString();
        }
        var results = await Task.WhenAll(first.CheckIn(ApiLocationAdmissionTests.Dto()),
            second.LogLocation(ApiLocationAdmissionTests.Dto()));
        Assert.All(results, result => Assert.IsType<OkObjectResult>(result));
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.Locations.CountAsync(l => l.UserId == user.Id && l.IdempotencyKey == key));
        Assert.Equal(0, gate.IdentityCount);

        var saturated = new ApiWorkAdmission(1, 1);
        using var held = saturated.TryAcquire(user.Id, out _);
        var replay = ApiLocationAdmissionTests.Controller(verify, saturated);
        replay.Request.Headers.Authorization = $"Bearer {user.Id}";
        replay.Request.Headers["Idempotency-Key"] = key.ToString();
        Assert.IsType<OkObjectResult>(await replay.CheckIn(ApiLocationAdmissionTests.Dto()));
        Assert.IsType<OkObjectResult>(await replay.LogLocation(ApiLocationAdmissionTests.Dto()));

        var other = await fixture.CreateUserAsync();
        await SeedToken(other);
        await using var otherDb = fixture.CreateContext();
        var independent = ApiLocationAdmissionTests.Controller(otherDb, gate);
        independent.Request.Headers.Authorization = $"Bearer {other.Id}";
        independent.Request.Headers["Idempotency-Key"] = key.ToString();
        Assert.IsType<OkObjectResult>(await independent.CheckIn(ApiLocationAdmissionTests.Dto()));
        Assert.Equal(1, await verify.Locations.CountAsync(l => l.UserId == other.Id && l.IdempotencyKey == key));
    }

    /// <summary>Seeds a fixture-owned token; user cleanup cascades to tokens and locations.</summary>
    private async Task SeedToken(ApplicationUser user)
    {
        await using var db = fixture.CreateContext();
        db.ApiTokens.Add(new ApiToken { User = null!, UserId = user.Id, Name = "phone", Token = user.Id });
        await db.SaveChangesAsync();
    }

    /// <summary>Holds both real first inserts until both actions have passed their replay lookup.</summary>
    private sealed class SaveBarrier : SaveChangesInterceptor
    {
        private int _arrived;
        private readonly TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _arrived) == 2) _both.TrySetResult();
            await _both.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }
}
