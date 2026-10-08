using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Validates the complete fresh migration chain and a representative last-schema invitation upgrade.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class GroupInvitationEmailMigrationPostgresTests(PostgresMigrationTestFixture fixture)
{
    private const string PreviousMigration = "20260924220353_StablePersonalCredentialCompanion";

    /// <summary>A freshly migrated database and current model have no email column or dependency on it.</summary>
    [PostgresFact]
    public async Task FreshDatabaseSupportsUserIdOnlyInvitations()
    {
        var owner = await fixture.CreateUserAsync();
        var target = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        var group = await new GroupService(db).CreateGroupAsync(owner.Id, $"Fresh-{Guid.NewGuid()}", null);
        try
        {
            Assert.Equal(0, await EmailColumnCountAsync(db));
            Assert.Null(db.Model.FindEntityType(typeof(GroupInvitation))!.FindProperty("InviteeEmail"));
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
            var service = new InvitationService(db);
            var invitation = await service.InviteUserAsync(group.Id, owner.Id, target.Id, null);
            Assert.Equal(target.Id, (await service.AcceptAsync(invitation.Token, target.Id)).UserId);
        }
        finally { await db.Groups.Where(g => g.Id == group.Id).ExecuteDeleteAsync(); }
    }

    /// <summary>Upgrade revokes only unresolved pending history; rollback cannot recover discarded emails or authority.</summary>
    [PostgresFact]
    public async Task UpgradeRetiresUnresolvedHistoryAndDropsEmailPermanently()
    {
        var owner = await fixture.CreateUserAsync();
        var target = await fixture.CreateUserAsync();
        await using var db = fixture.CreateContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(PreviousMigration);
        Assert.Equal(1, await EmailColumnCountAsync(db));
        var group = await new GroupService(db).CreateGroupAsync(owner.Id, $"Upgrade-{Guid.NewGuid()}", null);
        var ordinaryId = Guid.NewGuid();
        var unresolvedId = Guid.NewGuid();
        var declinedId = Guid.NewGuid();
        var created = DateTime.UtcNow.AddDays(-1);
        var expiry = created.AddDays(7);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "GroupInvitations"
                ("Id", "GroupId", "InviterUserId", "InviteeUserId", "InviteeEmail", "Token", "Status", "CreatedAt", "ExpiresAt", "RespondedAt")
            VALUES
                ({ordinaryId}, {group.Id}, {owner.Id}, {target.Id}, 'ordinary@example.test', 'ordinary-upgrade', 'Pending', {created}, {expiry}, NULL),
                ({unresolvedId}, {group.Id}, {owner.Id}, NULL, 'unresolved@example.test', 'unresolved-upgrade', 'Pending', {created}, {expiry}, NULL),
                ({declinedId}, {group.Id}, {owner.Id}, NULL, 'declined@example.test', 'declined-upgrade', 'Declined', {created}, {expiry}, {created});
            """);

        await migrator.MigrateAsync();

        Assert.Equal(0, await EmailColumnCountAsync(db));
        var rows = await db.GroupInvitations.AsNoTracking().Where(i => i.GroupId == group.Id).ToDictionaryAsync(i => i.Id);
        Assert.Equal(3, rows.Count);
        Assert.Equal(GroupInvitation.InvitationStatuses.Pending, rows[ordinaryId].Status);
        Assert.Equal(target.Id, rows[ordinaryId].InviteeUserId);
        Assert.Null(rows[ordinaryId].RespondedAt);
        Assert.Equal(GroupInvitation.InvitationStatuses.Revoked, rows[unresolvedId].Status);
        Assert.Null(rows[unresolvedId].InviteeUserId);
        Assert.NotNull(rows[unresolvedId].RespondedAt);
        Assert.Equal(GroupInvitation.InvitationStatuses.Declined, rows[declinedId].Status);
        Assert.Equal(created.Ticks / 10, rows[declinedId].RespondedAt!.Value.Ticks / 10);
        foreach (var row in rows.Values)
        {
            Assert.Equal(group.Id, row.GroupId);
            Assert.Equal(owner.Id, row.InviterUserId);
            Assert.Equal(created.Ticks / 10, row.CreatedAt.Ticks / 10);
            Assert.Equal(expiry.Ticks / 10, row.ExpiresAt!.Value.Ticks / 10);
        }
        await migrator.MigrateAsync(PreviousMigration);
        Assert.Equal(1, await EmailColumnCountAsync(db));
        Assert.Equal(0, await db.Database.SqlQuery<int>($"""
            SELECT COUNT(*)::int AS "Value" FROM "GroupInvitations"
            WHERE "GroupId" = {group.Id} AND "InviteeEmail" IS NOT NULL
            """).SingleAsync());
        Assert.Equal(GroupInvitation.InvitationStatuses.Revoked,
            (await db.GroupInvitations.AsNoTracking().SingleAsync(i => i.Id == unresolvedId)).Status);
        await migrator.MigrateAsync();
        Assert.Equal(0, await EmailColumnCountAsync(db));
        await transaction.RollbackAsync();
    }

    /// <summary>Queries the actual provider schema, including inside the migration transaction.</summary>
    private static Task<int> EmailColumnCountAsync(ApplicationDbContext db) => db.Database.SqlQuery<int>($"""
        SELECT COUNT(*)::int AS "Value" FROM information_schema.columns
        WHERE table_schema = 'public' AND table_name = 'GroupInvitations' AND column_name = 'InviteeEmail'
        """).SingleAsync();
}
