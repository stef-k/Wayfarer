using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Wayfarer.Models;

namespace Wayfarer.Services;

/// <summary>One concrete owner for persisted photo state, exact-context reads and durable orphan recovery.</summary>
public sealed class ProfilePhotoService(IServiceScopeFactory scopes, ProfilePhotoStorage storage, ILogger<ProfilePhotoService> logger)
{
    // Dedicated two-int namespace, separate from import locks and all hashed bigint user locks.
    private const int LockNamespace = 881;

    /// <summary>Issue a nonempty Identity stamp even for legacy absent-photo accounts.</summary>
    public async Task<ProfilePhotoState?> OwnerStateAsync(string owner, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await BeginAsync(db, ct);
        var user = await LockedOwnerAsync(db, owner, ct);
        if (user == null) return null;
        if (string.IsNullOrWhiteSpace(user.ConcurrencyStamp))
        {
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            await db.Users.Where(u => u.Id == owner).ExecuteUpdateAsync(p => p.SetProperty(u => u.ConcurrencyStamp, user.ConcurrencyStamp), ct);
        }
        await transaction.CommitAsync(ct);
        return State(user);
    }

    /// <summary>Compare at publication, update only photo fields/stamp, and never reactivate a candidate key.</summary>
    public async Task<ProfilePhotoState> MutateAsync(string owner, string expectedStateToken, byte[]? derivative, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedStateToken)) throw new ProfilePhotoException(400, "invalid_photo_state");
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        IDbContextTransaction? transaction = null;
        var commitStarted = false;
        Guid? old = null;
        try
        {
            transaction = await BeginAsync(db, ct);
            var user = await LockedOwnerAsync(db, owner, ct) ?? throw new ProfilePhotoException(404, "photo_not_found");
            if (!string.Equals(expectedStateToken, user.ConcurrencyStamp, StringComparison.Ordinal))
                throw new ProfilePhotoException(409, "photo_changed");
            old = user.ProfilePhotoVersion;
            var candidate = derivative == null ? (Guid?)null : Guid.NewGuid();
            if (candidate.HasValue) await storage.PublishAsync(candidate.Value, derivative!, ct);
            ct.ThrowIfCancellationRequested();
            user.ProfilePhotoVersion = candidate;
            user.ConcurrencyStamp = Guid.NewGuid().ToString();
            await db.Users.Where(u => u.Id == owner).ExecuteUpdateAsync(p => p
                .SetProperty(u => u.ProfilePhotoVersion, candidate).SetProperty(u => u.ConcurrencyStamp, user.ConcurrencyStamp), ct);
            ct.ThrowIfCancellationRequested();
            commitStarted = true;
            await transaction.CommitAsync(ct);
            // Targeted cleanup runs outside the request slot and always asks fresh DB authority.
            if (old.HasValue) QueueCleanup(old.Value);
            return State(user);
        }
        catch (Exception) when (commitStarted) { throw new ProfilePhotoException(503, "photo_outcome_unknown"); }
        finally
        {
            if (transaction != null)
            {
                // Cleanup has an independent deadline; no cancelled request token owns rollback.
                try { await transaction.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch { logger.LogWarning("Photo transaction cleanup was not confirmed."); }
            }
        }
    }

    /// <summary>Only the active owner and exact current version authorize owner bytes.</summary>
    public async Task<Guid?> OwnerVersionAsync(string owner, Guid version, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Id == owner && u.IsActive && u.ProfilePhotoVersion == version)
            .Select(u => u.ProfilePhotoVersion).SingleOrDefaultAsync(ct);
    }

    /// <summary>Both active accounts must belong to the exact unarchived Group; location sharing is independent.</summary>
    public async Task<Guid?> GroupVersionAsync(string caller, Guid group, string target, Guid version, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Id == target && u.IsActive && u.ProfilePhotoVersion == version
            && db.Users.Any(c => c.Id == caller && c.IsActive)
            && db.Groups.Any(g => g.Id == group && !g.IsArchived)
            && db.GroupMembers.Any(m => m.GroupId == group && m.UserId == caller && m.Status == GroupMember.MembershipStatuses.Active)
            && db.GroupMembers.Any(m => m.GroupId == group && m.UserId == target && m.Status == GroupMember.MembershipStatuses.Active))
            .Select(u => u.ProfilePhotoVersion).SingleOrDefaultAsync(ct);
    }

    /// <summary>Public access derives the owner only from this exact currently public Trip and retained consent.</summary>
    public async Task<Guid?> PublicVersionAsync(Guid trip, Guid version, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Trips.AsNoTracking().Where(t => t.Id == trip && t.IsPublic && t.User.IsActive
            && t.User.ShowProfilePhotoOnPublicTrips && t.User.ProfilePhotoVersion == version)
            .Select(t => t.User.ProfilePhotoVersion).SingleOrDefaultAsync(ct);
    }

    /// <summary>Open bytes only after persisted policy has been evaluated by the authentication adapter.</summary>
    public byte[]? Read(Guid version)
    {
        try
        {
            var bytes = storage.Read(version);
            if (bytes == null) logger.LogWarning("Referenced profile photo is missing.");
            return bytes;
        }
        catch (IOException) { logger.LogWarning("Profile photo storage read failed."); return null; }
    }

    /// <summary>Complete successive batches, with one fresh primary reference query under the publication lock per batch.</summary>
    public async Task SweepAsync(CancellationToken ct)
    {
        try { foreach (var batch in storage.Batches()) await SweepBatchAsync(batch, ct); }
        catch (Exception error) when (error is IOException or System.Data.Common.DbException)
        { logger.LogWarning("Profile photo cleanup deferred; storage/database authority unavailable."); }
    }

    /// <summary>No DB uncertainty authorizes unlink; unknown entries and IO failures remain retry evidence.</summary>
    private async Task SweepBatchAsync(string[] names, CancellationToken ct)
    {
        var candidates = names.Where(name => ProfilePhotoStorage.TryVersion(name, out _)).ToArray();
        if (candidates.Length != names.Length) logger.LogWarning("Unexpected profile photo storage entry retained.");
        if (candidates.Length == 0) return;
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await using var transaction = await BeginAsync(db, ct);
        var versions = candidates.Select(name => Guid.ParseExact(name[..32], "N")).Distinct().ToArray();
        var referenced = await db.Users.AsNoTracking().Where(u => u.ProfilePhotoVersion.HasValue && versions.Contains(u.ProfilePhotoVersion.Value))
            .Select(u => u.ProfilePhotoVersion!.Value).ToListAsync(ct);
        foreach (var name in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (referenced.Contains(Guid.ParseExact(name[..32], "N"))) continue;
            try { storage.Delete(name); }
            catch (IOException) { logger.LogWarning("Profile photo cleanup deferred."); }
        }
        await transaction.CommitAsync(ct);
    }

    /// <summary>A finite best-effort old-file cleanup cannot extend mutation response or request admission.</summary>
    private void QueueCleanup(Guid version)
    {
        _ = Task.Run(async () =>
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await SweepBatchAsync([version.ToString("N") + ".webp", version.ToString("N") + ".tmp"], deadline.Token); }
            catch { logger.LogWarning("Profile photo cleanup deferred."); }
        });
    }

    /// <summary>Bounded PostgreSQL transaction locks order small file publication and orphan authority.</summary>
    private static async Task<IDbContextTransaction> BeginAsync(ApplicationDbContext db, CancellationToken ct)
    {
        var transaction = await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'; SET LOCAL statement_timeout = '3s'", ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LockNamespace}, 1)", ct);
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    /// <summary>Reload under row lock; tracked whole-user snapshots never participate in photo publication.</summary>
    private static Task<ApplicationUser?> LockedOwnerAsync(ApplicationDbContext db, string owner, CancellationToken ct) =>
        db.Users.FromSqlInterpolated($"SELECT * FROM \"AspNetUsers\" WHERE \"Id\" = {owner} AND \"IsActive\" FOR UPDATE")
            .AsNoTracking().SingleOrDefaultAsync(ct);

    /// <summary>Owner-only concurrency evidence is distinct from immutable byte identity.</summary>
    private static ProfilePhotoState State(ApplicationUser user) => new(user.ConcurrencyStamp!,
        ProfilePhotoReference.Owner(user.ProfilePhotoVersion), user.ShowProfilePhotoOnPublicTrips);
}

/// <summary>Fixed public errors never carry paths, state tokens or decoder diagnostics.</summary>
public sealed class ProfilePhotoException(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Private recovery state, including an opaque nonempty token during absence.</summary>
public sealed record ProfilePhotoState(string StateToken, ProfilePhotoReference? ProfilePhoto, bool ShowProfilePhotoOnPublicTrips);

/// <summary>Context-scoped extensionless URL and immutable file identity; neither grants read authority.</summary>
public sealed record ProfilePhotoReference(string Version, string Url)
{
    public static ProfilePhotoReference? Owner(Guid? version) => version.HasValue
        ? new(version.Value.ToString("N"), $"/api/profile-photo/me/{version:N}") : null;
    public static ProfilePhotoReference? Group(Guid? version, Guid group, string user, bool mobile = false) => version.HasValue
        ? new(version.Value.ToString("N"), $"/api/{(mobile ? "mobile/" : "")}groups/{group}/members/{Uri.EscapeDataString(user)}/photo/{version:N}") : null;
    public static ProfilePhotoReference? Public(Guid? version, Guid trip) => version.HasValue
        ? new(version.Value.ToString("N"), $"/Public/Trips/{trip}/owner-photo/{version:N}") : null;
}

/// <summary>One server-rendered identity badge with a separately authoritative backend color.</summary>
public sealed record ProfilePhotoBadge(string? DisplayName, string? UserName, string? ColorHex, ProfilePhotoReference? Photo);
