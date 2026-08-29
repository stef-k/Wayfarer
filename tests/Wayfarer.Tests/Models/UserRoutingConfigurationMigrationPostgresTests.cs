using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Executes user-routing configuration migration history on a disposable PostgreSQL database.</summary>
[Collection(PostgresMigrationTestCollection.Name)]
public sealed class UserRoutingConfigurationMigrationPostgresTests(PostgresMigrationTestFixture fixture)
{
    private const string PreviousMigration = "20260819102433_RoutingProviderMinimumInterval";

    /// <summary>Proves migration backfill, future-user creation, constraints, FKs, and xmin recovery.</summary>
    [PostgresFact]
    public async Task RetainedConfiguration_MigrationConstraintsRelationshipsAndXmin_AreAuthoritative()
    {
        fixture.RequireAvailable();
        var legacyUserId = $"routing-legacy-{Guid.NewGuid():N}";
        var futureUserId = $"routing-future-{Guid.NewGuid():N}";
        var providerId = Guid.NewGuid();
        await using var context = fixture.CreateContext();
        var migrator = context.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync(PreviousMigration);
            await InsertUserAsync(context, legacyUserId);
            await migrator.MigrateAsync();
            Assert.Equal(1, await context.Set<UserRoutingConfiguration>().CountAsync(item => item.UserId == legacyUserId));
            Assert.Null((await context.Set<UserRoutingConfiguration>().AsNoTracking()
                .SingleAsync(item => item.UserId == legacyUserId)).SelectedProviderConfigurationId);

            await InsertUserAsync(context, futureUserId);
            Assert.Equal(1, await context.Set<UserRoutingConfiguration>().CountAsync(item => item.UserId == futureUserId));
            await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "UserRoutingConfigurations"
                SET "CredentialPresent" = TRUE, "CredentialCiphertext" = {{"ciphertext"}}
                WHERE "UserId" = {{legacyUserId}}
                """));

            await context.Database.ExecuteSqlInterpolatedAsync($$"""
                INSERT INTO "RoutingProviderConfigurations"
                    ("Id", "DisplayName", "AdapterType", "CredentialPresent", "CredentialRequired", "Enabled",
                     "ConfigurationVersion", "GenerationTimeoutSeconds", "ResponseSizeLimitBytes", "RequestsPerMinute",
                     "MinimumIntervalMilliseconds", "MaxConcurrency", "PersonalRoutingAccess")
                VALUES ({{providerId}}, {{"Personal fixture"}}, 1, FALSE, FALSE, TRUE, 1, 15, 1048576, 60, 0, 4, 2)
                """);
            foreach (var access in Enum.GetValues<PersonalRoutingAccess>())
            {
                await context.Database.ExecuteSqlInterpolatedAsync($$"""
                    UPDATE "RoutingProviderConfigurations" SET "PersonalRoutingAccess" = {{(int)access}}
                    WHERE "Id" = {{providerId}}
                    """);
                Assert.Equal(access, (await context.Set<RoutingProviderConfiguration>().AsNoTracking()
                    .SingleAsync(item => item.Id == providerId)).PersonalRoutingAccess);
            }
            foreach (var undefinedAccess in new[] { 999, -1 })
            {
                await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync($$"""
                    UPDATE "RoutingProviderConfigurations" SET "PersonalRoutingAccess" = {{undefinedAccess}}
                    WHERE "Id" = {{providerId}}
                    """));
                context.ChangeTracker.Clear();
                Assert.Equal(PersonalRoutingAccess.CredentialFree,
                    (await context.Set<RoutingProviderConfiguration>().AsNoTracking()
                        .SingleAsync(item => item.Id == providerId)).PersonalRoutingAccess);
                Assert.Equal(2, await context.Set<UserRoutingConfiguration>().CountAsync(item =>
                    item.UserId == legacyUserId || item.UserId == futureUserId));
            }
            await context.Database.ExecuteSqlInterpolatedAsync($$"""
                UPDATE "UserRoutingConfigurations" SET "SelectedProviderConfigurationId" = {{providerId}}
                WHERE "UserId" = {{legacyUserId}}
                """);
            await Assert.ThrowsAsync<PostgresException>(() => context.Database.ExecuteSqlInterpolatedAsync($$"""
                DELETE FROM "RoutingProviderConfigurations" WHERE "Id" = {{providerId}}
                """));

            await using var first = fixture.CreateContext();
            await using var stale = fixture.CreateContext();
            var current = await first.Set<UserRoutingConfiguration>().SingleAsync(item => item.UserId == legacyUserId);
            var staleCopy = await stale.Set<UserRoutingConfiguration>().SingleAsync(item => item.UserId == legacyUserId);
            current.IncrementVersion();
            await first.SaveChangesAsync();
            staleCopy.IncrementVersion();
            await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
            stale.ChangeTracker.Clear();
            Assert.Equal(current.ConfigurationVersion, (await stale.Set<UserRoutingConfiguration>().AsNoTracking()
                .SingleAsync(item => item.UserId == legacyUserId)).ConfigurationVersion);

            context.ChangeTracker.Clear();
            context.Users.Remove(await context.Users.SingleAsync(item => item.Id == legacyUserId));
            await context.SaveChangesAsync();
            Assert.False(await context.Set<UserRoutingConfiguration>().AnyAsync(item => item.UserId == legacyUserId));
        }
        finally
        {
            await migrator.MigrateAsync();
            await context.Database.ExecuteSqlInterpolatedAsync($$"""
                DELETE FROM "AspNetUsers" WHERE "Id" IN ({{legacyUserId}}, {{futureUserId}});
                DELETE FROM "RoutingProviderConfigurations" WHERE "Id" = {{providerId}};
                """);
        }
    }


    private static async Task InsertUserAsync(ApplicationDbContext context, string userId)
    {
        context.Users.Add(new ApplicationUser
        {
            Id = userId, UserName = userId, NormalizedUserName = userId.ToUpperInvariant(),
            DisplayName = "Routing fixture", IsActive = true
        });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
    }

}
