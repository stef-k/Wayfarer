using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Locks the bounded database-time and authority seams required by enrichment presentation.</summary>
public sealed class LocationEnrichmentBoundedInspectionContractTests
{
    [Fact]
    public void PresentationUsesDedicatedStatusAndAggregateOwners()
    {
        var projector = Read("Services", "LocationEnrichment", "LocationEnrichmentPresentationProjector.cs");
        var gate = Read("Services", "LocationProviders", "PersonalProviderContactGate.cs");

        Assert.Contains("IPersonalProviderStatusReader", projector);
        Assert.Contains("ILocationEnrichmentProgressQuery", projector);
        Assert.DoesNotContain("ToListAsync", projector);
        Assert.DoesNotContain("ToDictionaryAsync", projector);
        Assert.DoesNotContain("InspectPersistentGeocodingAsync", gate);
    }

    [Fact]
    public void StatusInspectionUsesOnePostgreSqlClockSnapshot()
    {
        var reader = Read("Services", "LocationProviders", "PersonalProviderStatusReader.cs");

        Assert.Equal(1, Count(reader, "DatabaseUtcNowAsync"));
        Assert.DoesNotContain("DateTime.UtcNow", reader);
        Assert.DoesNotContain("DateTimeOffset.UtcNow", reader);
        Assert.Contains("AddSeconds(5)", reader);
    }

    [Fact]
    public void StartUsesTheSharedRunnableAggregatePredicate()
    {
        var handoff = Read("Services", "LocationEnrichment", "ImportEnrichmentHandoff.cs");

        Assert.Contains("ILocationEnrichmentProgressQuery", handoff);
        Assert.DoesNotContain("private Task<bool> HasCandidateAsync", handoff);
        Assert.Contains("Take(RepairCommandLimit).ToListAsync", handoff);
    }

    private static int Count(string value, string fragment) =>
        value.Split(fragment, StringSplitOptions.None).Length - 1;

    private static string Read(params string[] parts) => File.ReadAllText(Path.GetFullPath(
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts])));
}
