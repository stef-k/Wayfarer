using Xunit;

namespace Wayfarer.Tests.Documentation;

/// <summary>Prevents canonical enrichment documentation from drifting behind the shipped workflow.</summary>
public sealed class LocationEnrichmentDocumentationTests
{
    [Fact]
    public void ImportingGuideDoesNotAdvertiseUnavailableRegenerateAction()
    {
        var guide = File.ReadAllText(RepositoryFile("docs", "07-Importing-Exporting.md"));

        Assert.DoesNotContain("**Regenerate**", guide);
        Assert.Contains("Google Timeline JSON", guide);
        Assert.Contains("generic GeoJSON is not supported", guide, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CanonicalDocsStateCapacityPrivacyAndReleaseOrdering()
    {
        var docs = string.Join('\n', Directory.GetFiles(RepositoryFile("docs"), "*.md")
            .Select(File.ReadAllText).Append(File.ReadAllText(RepositoryFile("README.md"))));

        Assert.Contains("2,500", docs);
        Assert.Contains("content-free SSE", docs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("#502 → #507 → #500", docs);
    }

    private static string RepositoryFile(params string[] parts) => Path.GetFullPath(
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts]));
}
