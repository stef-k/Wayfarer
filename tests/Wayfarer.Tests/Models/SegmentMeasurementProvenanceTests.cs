using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Wayfarer.Migrations;
using Wayfarer.Models;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Models;

/// <summary>Defines the persisted Automatic/Manual duration-provenance contract for issue 405.</summary>
public sealed class SegmentMeasurementProvenanceTests : TestBase
{
    /// <summary>Proves new segments default to explicit Automatic duration ownership.</summary>
    [Fact]
    public void Segment_DefaultsDurationSourceToAutomatic()
    {
        var segment = new Segment();

        Assert.Equal(EstimatedDurationSource.Automatic, segment.EstimatedDurationSource);
    }

    /// <summary>Proves EF stores provenance as a required integer with a database default and bounded constraint.</summary>
    [Fact]
    public void RuntimeModel_MapsRequiredIntegerProvenance()
    {
        using var context = CreateDbContext();
        var entity = context.Model.FindEntityType(typeof(Segment))!;
        var property = entity.FindProperty(nameof(Segment.EstimatedDurationSource))!;

        Assert.False(property.IsNullable);
        Assert.Equal(typeof(int), property.GetProviderClrType());
        Assert.Equal(EstimatedDurationSource.Automatic, property.GetDefaultValue());
        var designEntity = context.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Segment))!;
        Assert.Contains(designEntity.GetCheckConstraints(), constraint =>
            constraint.Name == "CK_Segments_EstimatedDurationSource"
            && constraint.Sql.Contains("EstimatedDurationSource", StringComparison.Ordinal)
            && constraint.Sql.Contains("0", StringComparison.Ordinal)
            && constraint.Sql.Contains("1", StringComparison.Ordinal));
    }

    /// <summary>Proves the only defined durable values are Automatic zero and Manual one.</summary>
    [Fact]
    public void Enum_UsesStableDatabaseValues()
    {
        Assert.Equal(0, (int)EstimatedDurationSource.Automatic);
        Assert.Equal(1, (int)EstimatedDurationSource.Manual);
        Assert.Equal(2, Enum.GetValues<EstimatedDurationSource>().Length);
    }

    /// <summary>Proves legacy non-null durations are classified before the database constraint is installed.</summary>
    [Fact]
    public void Migration_UpBackfillsBeforeConstraintWithoutRecalculatingMeasurements()
    {
        var operations = Operations("Up");
        var addIndex = operations.FindIndex(operation => operation is AddColumnOperation);
        var backfillIndex = operations.FindIndex(operation => operation is SqlOperation);
        var constraintIndex = operations.FindIndex(operation => operation is AddCheckConstraintOperation);

        Assert.True(addIndex >= 0 && addIndex < backfillIndex && backfillIndex < constraintIndex);
        var column = Assert.IsType<AddColumnOperation>(operations[addIndex]);
        Assert.Equal("EstimatedDurationSource", column.Name);
        Assert.False(column.IsNullable);
        Assert.Equal(0, column.DefaultValue);
        var sql = Assert.IsType<SqlOperation>(operations[backfillIndex]).Sql;
        Assert.Contains("SET \"EstimatedDurationSource\" = 1", sql);
        Assert.Contains("WHERE \"EstimatedDuration\" IS NOT NULL", sql);
        Assert.DoesNotContain("EstimatedDistanceKm\" =", sql);
        Assert.DoesNotContain("EstimatedDuration\" =", sql);
    }

    /// <summary>Proves downgrade removes only the constraint and provenance column.</summary>
    [Fact]
    public void Migration_DownRemovesOnlyProvenanceSchema()
    {
        var operations = Operations("Down");

        Assert.Collection(operations,
            operation => Assert.Equal("CK_Segments_EstimatedDurationSource", Assert.IsType<DropCheckConstraintOperation>(operation).Name),
            operation => Assert.Equal("EstimatedDurationSource", Assert.IsType<DropColumnOperation>(operation).Name));
    }

    private static List<MigrationOperation> Operations(string methodName)
    {
        var migration = new AddSegmentMeasurementProvenance();
        var builder = new MigrationBuilder("Npgsql.EntityFrameworkCore.PostgreSQL");
        typeof(AddSegmentMeasurementProvenance)
            .GetMethod(methodName, BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(migration, [builder]);
        return builder.Operations;
    }
}
