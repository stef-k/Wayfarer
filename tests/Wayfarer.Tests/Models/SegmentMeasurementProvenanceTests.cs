using Microsoft.EntityFrameworkCore;
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
        Assert.Equal(0, property.GetDefaultValue());
        Assert.Contains(entity.GetCheckConstraints(), constraint =>
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
}
