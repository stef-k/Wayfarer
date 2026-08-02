using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Wayfarer.Migrations;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Verifies the generated waypoint migration operations without claiming provider execution.</summary>
public sealed class SegmentWaypointMigrationTests
{
    /// <summary>Proves Up adds only the waypoint table and its supporting indexes.</summary>
    [Fact]
    public void Up_CreatesFocusedWaypointSchema()
    {
        var operations = Operations("Up");
        var table = Assert.Single(operations.OfType<CreateTableOperation>());

        Assert.Equal("SegmentWaypoints", table.Name);
        Assert.Equal(["SegmentId", "PlaceId"], table.PrimaryKey!.Columns);
        Assert.Equal(2, table.ForeignKeys.Count);
        Assert.Equal(2, table.CheckConstraints.Count);
        Assert.Equal(3, operations.OfType<CreateIndexOperation>().Count());
        Assert.DoesNotContain(operations, operation => operation is AddColumnOperation or AlterColumnOperation or SqlOperation);
    }

    /// <summary>Proves Down removes only the schema introduced by issue 404.</summary>
    [Fact]
    public void Down_DropsOnlyWaypointTable()
    {
        var operation = Assert.Single(Operations("Down"));
        var drop = Assert.IsType<DropTableOperation>(operation);

        Assert.Equal("SegmentWaypoints", drop.Name);
    }

    private static List<MigrationOperation> Operations(string methodName)
    {
        var migration = new AddSegmentWaypoints();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddSegmentWaypoints)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);
        return builder.Operations;
    }
}
