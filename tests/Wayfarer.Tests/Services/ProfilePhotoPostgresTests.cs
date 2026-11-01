using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Moq;
using Wayfarer.Models;
using Wayfarer.Models.Options;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Real owner-row/stamp ordering and durable orphan cleanup at the PostgreSQL/filesystem seam.</summary>
[Collection(PostgresImportTestCollection.Name)]
public sealed class ProfilePhotoPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Definite precommit failure, lost acknowledgement and dead session converge by fresh authority.</summary>
    [PostgresTheory]
    [InlineData("before")]
    [InlineData("after")]
    [InlineData("session")]
    public async Task PublicationFailureRetainsWinnerAndNeverReusesCandidate(string failure)
    {
        var user = await fixture.CreateUserAsync();
        using var storage = new PhotoTestStorage();
        using var normal = new ServiceCollection().AddScoped(_ => fixture.CreateContext()).BuildServiceProvider();
        var photos = new ProfilePhotoService(normal.GetRequiredService<IServiceScopeFactory>(), storage.Store, NullLogger<ProfilePhotoService>.Instance);
        var state = await photos.OwnerStateAsync(user.Id, default);
        var first = await photos.MutateAsync(user.Id, state!.StateToken, "old"u8.ToArray(), default);
        IInterceptor fault = failure == "session" ? new LostSession() : new CommitFault(failure == "after");
        using var failing = new ServiceCollection().AddScoped(_ => fixture.CreateContext(fault)).BuildServiceProvider();
        var attempt = new ProfilePhotoService(failing.GetRequiredService<IServiceScopeFactory>(), storage.Store, NullLogger<ProfilePhotoService>.Instance);
        var error = await Record.ExceptionAsync(() => attempt.MutateAsync(user.Id, first.StateToken, "candidate"u8.ToArray(), default));
        Assert.NotNull(error);
        if (failure == "after") Assert.Equal("photo_outcome_unknown", Assert.IsType<ProfilePhotoException>(error).Code);
        var refreshed = await photos.OwnerStateAsync(user.Id, default);
        Assert.Equal(failure == "after", refreshed!.ProfilePhoto!.Version != first.ProfilePhoto!.Version);
        await photos.SweepAsync(default);
        Assert.Single(Directory.GetFiles(storage.Store.Root));
        Assert.NotNull(photos.Read(Guid.ParseExact(refreshed.ProfilePhoto.Version, "N")));
        // A subsequent deliberate save generates another identity, never reattaching the retired candidate.
        var next = await photos.MutateAsync(user.Id, refreshed.StateToken, "fresh"u8.ToArray(), default);
        Assert.NotEqual(refreshed.ProfilePhoto.Version, next.ProfilePhoto!.Version);
    }

    /// <summary>Identity's whole-user update cannot overwrite a photo committed since its original read.</summary>
    [PostgresFact]
    public async Task StaleWholeUserAndUnrelatedIdentityUpdateRespectConcurrency()
    {
        var user = await fixture.CreateUserAsync();
        using var storage = new PhotoTestStorage();
        using var provider = new ServiceCollection().AddScoped(_ => fixture.CreateContext()).BuildServiceProvider();
        var photos = new ProfilePhotoService(provider.GetRequiredService<IServiceScopeFactory>(), storage.Store, NullLogger<ProfilePhotoService>.Instance);
        var state = await photos.OwnerStateAsync(user.Id, default);
        await using var db = fixture.CreateContext();
        var stale = await db.Users.SingleAsync(u => u.Id == user.Id);
        var winner = await photos.MutateAsync(user.Id, state!.StateToken, "winner"u8.ToArray(), default);
        stale.DisplayName = "Stale profile form";
        using var store = new UserStore<ApplicationUser, IdentityRole, ApplicationDbContext, string>(db);
        Assert.False((await store.UpdateAsync(stale)).Succeeded);
        var current = await photos.OwnerStateAsync(user.Id, default);
        Assert.Equal(winner, current);
        await using (var fresh = fixture.CreateContext())
            await fresh.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(p => p.SetProperty(u => u.ConcurrencyStamp, Guid.NewGuid().ToString()));
        var conflict = await Assert.ThrowsAsync<ProfilePhotoException>(() => photos.MutateAsync(user.Id, winner.StateToken, null, default));
        Assert.Equal("photo_changed", conflict.Code);
    }

    /// <summary>File publication failure leaves the original current file and pointer intact.</summary>
    [PostgresFact]
    public async Task StorageFailurePreservesCurrentPhotoAndCleanupRetriesAcrossServiceRestart()
    {
        var user = await fixture.CreateUserAsync();
        using var storage = new PhotoTestStorage();
        using var provider = new ServiceCollection().AddScoped(_ => fixture.CreateContext()).BuildServiceProvider();
        var photos = new ProfilePhotoService(provider.GetRequiredService<IServiceScopeFactory>(), storage.Store, NullLogger<ProfilePhotoService>.Instance);
        var state = await photos.OwnerStateAsync(user.Id, default);
        var current = await photos.MutateAsync(user.Id, state!.StateToken, "winner"u8.ToArray(), default);
        var held = storage.Store.Root + "-held";
        Directory.Move(storage.Store.Root, held); File.WriteAllText(storage.Store.Root, "unavailable directory");
        try { await Assert.ThrowsAsync<IOException>(() => photos.MutateAsync(user.Id, current.StateToken, "replacement"u8.ToArray(), default)); }
        finally { File.Delete(storage.Store.Root); Directory.Move(held, storage.Store.Root); }
        Assert.Equal(current, await photos.OwnerStateAsync(user.Id, default));
        var orphan = Guid.NewGuid().ToString("N") + ".tmp";
        var path = Path.Combine(storage.Store.Root, orphan);
        File.CreateSymbolicLink(path, Path.Combine(storage.Store.Root, current.ProfilePhoto!.Version + ".webp"));
        await photos.SweepAsync(default); Assert.True(File.Exists(path));
        File.Delete(path); File.WriteAllText(path, "retryable orphan");
        var restarted = new ProfilePhotoService(provider.GetRequiredService<IServiceScopeFactory>(), storage.Store, NullLogger<ProfilePhotoService>.Instance);
        await restarted.SweepAsync(default);
        Assert.False(File.Exists(path)); Assert.NotNull(restarted.Read(Guid.ParseExact(current.ProfilePhoto.Version, "N")));
    }

    /// <summary>Repeated absence never validates a delayed upload or stale removal; consent survives.</summary>
    [PostgresFact]
    public async Task RepeatedAbsenceRejectsStaleIntentAndSweepRetainsOnlyCommittedFiles()
    {
        var user = await fixture.CreateUserAsync();
        await using (var db = fixture.CreateContext())
        {
            await db.Users.Where(u => u.Id == user.Id).ExecuteUpdateAsync(p => p
                .SetProperty(u => u.ShowProfilePhotoOnPublicTrips, true).SetProperty(u => u.ConcurrencyStamp, (string?)null));
        }
        using var storage = new PhotoTestStorage();
        using var services = new ServiceCollection().AddScoped(_ => fixture.CreateContext()).BuildServiceProvider();
        var photos = new ProfilePhotoService(services.GetRequiredService<IServiceScopeFactory>(), storage.Store,
            NullLogger<ProfilePhotoService>.Instance);
        var absent = await photos.OwnerStateAsync(user.Id, default);
        Assert.False(string.IsNullOrEmpty(absent!.StateToken));
        var uploaded = await photos.MutateAsync(user.Id, absent.StateToken, "normalized"u8.ToArray(), default);
        Assert.NotNull(uploaded.ProfilePhoto);
        Assert.True(uploaded.ShowProfilePhotoOnPublicTrips);
        var removed = await photos.MutateAsync(user.Id, uploaded.StateToken, null, default);
        Assert.Null(removed.ProfilePhoto);
        Assert.NotEqual(uploaded.StateToken, removed.StateToken);
        var conflict = await Assert.ThrowsAsync<ProfilePhotoException>(() => photos.MutateAsync(user.Id,
            absent.StateToken, "delayed"u8.ToArray(), default));
        Assert.Equal("photo_changed", conflict.Code);
        await Assert.ThrowsAsync<ProfilePhotoException>(() => photos.MutateAsync(user.Id, uploaded.StateToken, null, default));
        var again = await photos.MutateAsync(user.Id, removed.StateToken, null, default);
        Assert.NotEqual(removed.StateToken, again.StateToken);
        var current = await photos.MutateAsync(user.Id, again.StateToken, "winner"u8.ToArray(), default);
        await photos.SweepAsync(default);
        Assert.Single(Directory.GetFiles(storage.Store.Root));
        await using (var db = fixture.CreateContext()) await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync();
        Assert.Null(await photos.OwnerVersionAsync(user.Id, Guid.ParseExact(current.ProfilePhoto!.Version, "N"), default));
        await photos.SweepAsync(default);
        Assert.Empty(Directory.GetFiles(storage.Store.Root));
    }

    private sealed class CommitFault(bool after) : DbTransactionInterceptor
    {
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) =>
            after ? ValueTask.FromResult(result) : throw new IOException("Interrupted before commit.");
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData,
            CancellationToken cancellationToken = default) => throw new IOException("Commit acknowledgement lost.");
    }

    private sealed class LostSession : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("UPDATE \"AspNetUsers\"", StringComparison.Ordinal)) await command.Connection!.CloseAsync();
            return result;
        }
    }
}

/// <summary>Task-owned private storage under a disposable directory; never points at configured application data.</summary>
internal sealed class PhotoTestStorage : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "wayfarer-photo-" + Guid.NewGuid().ToString("N"));
    internal ProfilePhotoStorage Store { get; }
    internal PhotoTestStorage()
    {
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "uploads"));
        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(directory);
        environment.SetupGet(e => e.EnvironmentName).Returns("Development");
        var paths = new StoragePaths(Options.Create(new StorageOptions { DataRoot = directory }), environment.Object);
        Store = new ProfilePhotoStorage(paths);
    }
    public void Dispose() { Directory.Delete(directory, true); }
}
