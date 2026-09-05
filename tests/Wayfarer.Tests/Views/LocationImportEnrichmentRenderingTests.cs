using Xunit;

namespace Wayfarer.Tests.Views;

/// <summary>Locks the accessible content-safe enrichment surface at its Razor owner.</summary>
public sealed class LocationImportEnrichmentRenderingTests
{
    [Fact]
    public void EnrichmentUsesSeparateSemanticStatusAndStateSpecificForms()
    {
        var source = File.ReadAllText(ViewPath());

        Assert.Contains("aria-labelledby=\"enrichment-heading\"", source);
        Assert.Contains("<dl class=\"row\" aria-label=\"Durable enrichment status\">", source);
        Assert.Contains("id=\"enrichment-status\" role=\"status\"", source);
        Assert.Contains("aria-label=\"Enrichment actions\"", source);
        Assert.Contains("@if (enrichment.Start.Visible)", source);
        Assert.Contains("@if (enrichment.RetryDeferred.Visible)", source);
        Assert.Contains("asp-action=\"StartEnrichment\" method=\"post\"", source);
        Assert.Contains("Start enrichment", source);
        Assert.Contains("Retry deferred enrichment", source);
    }

    [Fact]
    public void EnrichmentExplainsPreservationProviderUsageAndUtcTime()
    {
        var source = File.ReadAllText(ViewPath());

        Assert.Contains("Imported Locations remain safe", source);
        Assert.Contains("Cancelling enrichment does not cancel or delete imports", source);
        Assert.Contains("share Wayfarer's rolling pool", source);
        Assert.Contains("provider account is not visible", source);
        Assert.Contains("independent monthly meter", source);
        Assert.Contains("@if (enrichment.RepairsWithoutLocality > 0)", source);
        Assert.Contains("no automatic retry is scheduled for that outcome", source);
        Assert.Contains("Incomplete addresses describe stored fields", source);
        Assert.Contains("Awaiting recovery", source);
        Assert.Contains("yyyy-MM-dd HH:mm 'UTC'", source);
        Assert.DoesNotContain("ProtectedCredential", source);
        Assert.DoesNotContain("Coordinates", source);
    }

    [Fact]
    public void ProviderCapacityReturnUsesItsOwnConditionalSemanticUtcRow()
    {
        var source = File.ReadAllText(ViewPath());
        var providerReturn = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);
        DateTime? nextAttempt = null;

        Assert.Equal("2026-09-01T00:00:00.0000000Z", providerReturn.ToString("O"));
        Assert.Equal("2026-09-01 00:00 UTC", providerReturn.ToString("yyyy-MM-dd HH:mm 'UTC'"));
        Assert.Null(nextAttempt);
        Assert.Contains("@if (enrichment.ProviderNextAvailableAtUtc.HasValue)", source);
        Assert.Contains("<dt class=\"col-sm-4\">Provider capacity returns</dt>", source);
        Assert.Contains("<time datetime=\"@enrichment.ProviderNextAvailableAtUtc.Value.ToString(\"O\")\">", source);
        Assert.Contains("@enrichment.ProviderNextAvailableAtUtc.Value.ToString(\"yyyy-MM-dd HH:mm 'UTC'\")", source);
    }

    private static string ViewPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "Areas", "User", "Views", "LocationImport", "Index.cshtml"));
}
