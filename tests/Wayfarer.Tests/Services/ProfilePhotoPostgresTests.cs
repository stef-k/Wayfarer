using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Microsoft.AspNetCore.TestHost;
using System.Net.Http.Json;
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
    /// <summary>Both managed erasure adapters commit removal of the same photo reference, without photo hooks.</summary>
    [PostgresTheory]
    [InlineData("Admin")]
    [InlineData("Manager")]
    public async Task ManagedErasureAdaptersCommitRowDeletion(string role)
    {
        var caller = await fixture.CreateUserAsync();
        var target = await fixture.CreateUserAsync();
        using var storage = new PhotoTestStorage();
        var version = Guid.NewGuid();
        await storage.Store.PublishAsync(version, "normalized"u8.ToArray(), default);
        await using var db = fixture.CreateContext();
        var userRole = await db.Roles.SingleOrDefaultAsync(r => r.Name == "User");
        var createdRole = userRole == null;
        userRole ??= new IdentityRole { Id = "photo-role-" + Guid.NewGuid(), Name = "User", NormalizedName = "USER" };
        if (createdRole) db.Roles.Add(userRole);
        db.UserRoles.Add(new IdentityUserRole<string> { UserId = target.Id, RoleId = userRole.Id });
        await db.SaveChangesAsync();
        await db.Users.Where(u => u.Id == target.Id).ExecuteUpdateAsync(p => p.SetProperty(u => u.ProfilePhotoVersion, (Guid?)version));
        try
        {
            var root = Path.GetDirectoryName(Path.GetDirectoryName(storage.Store.Root))!;
            await using var app = await IdentityRouteHost.StartAsync(db, root, services => services.AddSingleton(storage.Store));
            using var client = app.GetTestClient();
            var cookie = IdentityRouteHost.Cookie(app.Services, caller.Id, role);
            client.DefaultRequestHeaders.Add("Cookie", cookie);
            var token = await IdentityRouteHost.AntiforgeryAsync(client);
            var afCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
            client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + afCookie);
            client.DefaultRequestHeaders.Add("RequestVerificationToken", token);
            using var result = await client.PostAsync($"/{role}/Users/DeleteConfirmed", new FormUrlEncodedContent(new Dictionary<string, string> { ["id"] = target.Id }));
            Assert.Equal(System.Net.HttpStatusCode.Redirect, result.StatusCode);
            await using var fresh = fixture.CreateContext();
            Assert.False(await fresh.Users.AnyAsync(u => u.Id == target.Id));
        }
        finally
        {
            await using var cleanup = fixture.CreateContext();
            await cleanup.UserRoles.Where(link => link.UserId == target.Id).ExecuteDeleteAsync();
            if (createdRole) await cleanup.Roles.Where(r => r.Id == userRole.Id).ExecuteDeleteAsync();
        }
    }

    /// <summary>Real cookie/antiforgery/multipart/helper publication joins the independently proven lower seams.</summary>
    [PostgresFact]
    public async Task RoutedMultipartUploadPublishesAndIdentitySelfErasureRevokesBeforeSweep()
    {
        var user = await fixture.CreateUserAsync();
        using var storage = new PhotoTestStorage();
        using var provider = new ServiceCollection().AddScoped(_ => fixture.CreateContext()).BuildServiceProvider();
        var photos = new ProfilePhotoService(provider.GetRequiredService<IServiceScopeFactory>(), storage.Store, NullLogger<ProfilePhotoService>.Instance);
        var root = Path.GetDirectoryName(Path.GetDirectoryName(storage.Store.Root))!;
        await using var db = fixture.CreateContext();
        if (!await db.ApplicationSettings.AnyAsync()) db.ApplicationSettings.Add(new ApplicationSettings());
        await db.SaveChangesAsync();
        await using var app = await IdentityRouteHost.StartAsync(db, root, services =>
        {
            services.AddSingleton(storage.Store);
            services.AddSingleton(photos);
        });
        using var client = app.GetTestClient();
        var cookie = IdentityRouteHost.Cookie(app.Services, user.Id);
        client.DefaultRequestHeaders.Add("Cookie", cookie);
        var antiforgery = await IdentityRouteHost.AntiforgeryAsync(client);
        var afCookie = client.DefaultRequestHeaders.GetValues("Cookie").Single();
        client.DefaultRequestHeaders.Remove("Cookie"); client.DefaultRequestHeaders.Add("Cookie", cookie + "; " + afCookie);
        client.DefaultRequestHeaders.Add("RequestVerificationToken", antiforgery);
        var state = await client.GetFromJsonAsync<ProfilePhotoState>("/api/profile-photo/me");
        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(await File.ReadAllBytesAsync(Path.Combine(Wayfarer.Tests.Util.ImageHelperSupervisorTests.Fixtures, "orientation-7.png"))), "photo", "photo.png");
        body.Add(new StringContent(state!.StateToken), "expectedStateToken");
        body.Add(new StringContent("0"), "cropX"); body.Add(new StringContent("8"), "cropY"); body.Add(new StringContent("31"), "cropSize");
        using var response = await client.PostAsync("/api/profile-photo/me", body);
        Assert.True(response.IsSuccessStatusCode, response.IsSuccessStatusCode ? "" : await response.Content.ReadAsStringAsync());
        var saved = await response.Content.ReadFromJsonAsync<ProfilePhotoState>();
        Assert.NotNull(saved!.ProfilePhoto);
        using var erased = await client.PostAsync("/Identity/Account/Manage/DeletePersonalData", new FormUrlEncodedContent([]));
        Assert.Equal(System.Net.HttpStatusCode.Redirect, erased.StatusCode);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.Users.AnyAsync(u => u.Id == user.Id));
        Assert.Null(await photos.OwnerVersionAsync(user.Id, Guid.ParseExact(saved.ProfilePhoto.Version, "N"), default));
        await photos.SweepAsync(default);
        Assert.Empty(Directory.GetFiles(storage.Store.Root));
    }

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
        using (var uncertain = new ServiceCollection().AddScoped(_ => fixture.CreateContext(new ReferenceLookupFailure())).BuildServiceProvider())
        {
            var unavailable = new ProfilePhotoService(uncertain.GetRequiredService<IServiceScopeFactory>(), storage.Store, NullLogger<ProfilePhotoService>.Instance);
            await unavailable.SweepAsync(default);
            Assert.True(File.Exists(path));
            Assert.NotNull(photos.Read(Guid.ParseExact(current.ProfilePhoto.Version, "N")));
        }
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
        // A restored temp with the current UUID is still orphaned: only .webp is referenced.
        File.WriteAllText(Path.Combine(storage.Store.Root, current.ProfilePhoto!.Version + ".tmp"), "restored orphan");
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

    private sealed class ReferenceLookupFailure : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default) =>
            throw new IOException("Reference authority unavailable.");
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
