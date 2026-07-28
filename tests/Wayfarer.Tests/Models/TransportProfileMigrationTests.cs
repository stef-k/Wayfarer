using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wayfarer.Migrations;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Verifies the provider migration's deterministic data-reconciliation contract without claiming database execution.</summary>
public sealed class TransportProfileMigrationTests
{
    /// <summary>Proves schema, seed/reconciliation SQL, and referential compatibility occur in the required order.</summary>
    [Fact]
    public void Up_SeedsAndReconcilesBeforeAddingForeignKey()
    {
        var operations = Operations();
        var tableIndex = operations.FindIndex(operation => operation is CreateTableOperation create && create.Name == "TransportProfiles");
        var sqlIndex = operations.FindIndex(operation => operation is SqlOperation);
        var foreignKeyIndex = operations.FindIndex(operation => operation is AddForeignKeyOperation);

        Assert.True(tableIndex >= 0 && tableIndex < sqlIndex && sqlIndex < foreignKeyIndex);
        var sql = Assert.Single(operations.OfType<SqlOperation>()).Sql;
        Assert.Contains("'walk', 'Walk'", sql);
        Assert.Contains("'high-speed-train', 'High-speed train'", sql);
        Assert.All(TestDataFixtures.CreateTransportProfiles(), profile => Assert.Contains($"'{profile.Key}'", sql));
        Assert.Contains("Inactive compatibility profile", sql);
        Assert.Contains("btrim(\"Mode\") <> ''", sql);
        Assert.Contains("SET \"TransportProfileId\"", sql);
        Assert.Contains("TR_Segments_TransportProfile", sql);
        Assert.Contains("left(btrim(\"Mode\"), 112) AS label", sql);
        Assert.Contains("'Legacy: ' || label", sql);
        Assert.Contains("'Legacy: ' || left(btrim(NEW.\"Mode\"), 112)", sql);
        Assert.Contains("BEFORE INSERT OR UPDATE OF \"Mode\", \"TransportProfileId\"", sql);
        Assert.Contains("public.\"TransportProfiles\"", sql);
        Assert.Contains("public.\"Segments\"", sql);
        Assert.DoesNotContain("SET \"Mode\"", sql);
    }

    private static List<MigrationOperation> Operations()
    {
        var migration = new AdminManagedTransportProfiles();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AdminManagedTransportProfiles)
            .GetMethod("Up", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);
        return builder.Operations;
    }
}
