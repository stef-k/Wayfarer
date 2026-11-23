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
        Assert.Contains("<dl class=\"row\" aria-label=\"@T[\"Transfers.EnrichmentStatus\"]\">", source);
        Assert.Contains("id=\"enrichment-status\" role=\"status\"", source);
        Assert.Contains("aria-label=\"@T[\"Transfers.EnrichmentActions\"]\"", source);
        Assert.Contains("@if (enrichment.Start.Visible)", source);
        Assert.Contains("@if (enrichment.RetryDeferred.Visible)", source);
        Assert.Contains("asp-action=\"StartEnrichment\" method=\"post\"", source);
        Assert.Contains("Transfers.StartEnrichment", source);
        Assert.Contains("Transfers.RetryEnrichment", source);
    }

    [Fact]
    public void EnrichmentExplainsPreservationProviderUsageAndUtcTime()
    {
        var source = File.ReadAllText(ViewPath());

        Assert.Contains("Transfers.EnrichmentHelp", source);
        Assert.Contains("Transfers.ProviderUsageHelp", source);
        Assert.Contains("@if (enrichment.RepairsWithoutLocality > 0)", source);
        Assert.Contains("Transfers.RepairsWithoutLocality", source);
        Assert.Contains("Transfers.IncompleteHelp", source);
        Assert.Contains("Transfers.AwaitingRecovery", source);
        Assert.Contains("LocationTransferText.UtcDate", source);
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
        Assert.Contains("<dt class=\"col-sm-4\">@T[\"Transfers.CapacityReturns\"]</dt>", source);
        Assert.Contains("<time datetime=\"@enrichment.ProviderNextAvailableAtUtc.Value.ToString(\"O\", CultureInfo.InvariantCulture)\">", source);
        Assert.Contains("@LocationTransferText.UtcDate(T, enrichment.ProviderNextAvailableAtUtc.Value)", source);
    }

    /// <summary>The list uses the tested cross-platform safe projection rather than native path parsing.</summary>
    [Fact]
    public void ImportHistory_UsesSafeBasenameProjection()
    {
        var source = File.ReadAllText(ViewPath());
        Assert.Contains("LocationImportStagedFiles.DisplayName(import.FilePath)", source);
        Assert.DoesNotContain("Path.GetFileName(import.FilePath)", source);
    }

    private static string ViewPath() => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "Areas", "User", "Views", "LocationImport", "Index.cshtml"));
}
