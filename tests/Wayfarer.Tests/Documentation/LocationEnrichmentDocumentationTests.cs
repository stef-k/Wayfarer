using Xunit;

namespace Wayfarer.Tests.Documentation;

/// <summary>Prevents canonical enrichment documentation from drifting behind the shipped workflow.</summary>
public sealed class LocationEnrichmentDocumentationTests
{
    [Fact]
    public void CanonicalDocsDescribeOnlyAuthenticatedProtectedImportSseRoute()
    {
        var docs = Directory.GetFiles(RepositoryFile("docs"), "*.md")
            .Select(File.ReadAllText).Append(File.ReadAllText(RepositoryFile("README.md")))
            .ToArray();
        var combined = string.Join(Environment.NewLine, docs);

        Assert.DoesNotContain("/api/sse/stream/import-progress", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"/api/sse/stream/(?:[^\s`|)]*(?:import|enrichment)[^\s`|)]*)",
            combined.ToLowerInvariant());
        Assert.Contains("/api/sse/import", combined, StringComparison.Ordinal);
        Assert.Contains("authenticated", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NameIdentifier", combined, StringComparison.Ordinal);
        Assert.Contains("content-free", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("relational", combined, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ArchitectureSeparatesGroupAndProtectedImportStreamDomains()
    {
        var architecture = File.ReadAllText(RepositoryFile("docs", "15-Architecture.md"));
        var groupStart = architecture.IndexOf("### Legacy group notification streams", StringComparison.Ordinal);
        var protectedStart = architecture.IndexOf("### Protected import and enrichment stream", StringComparison.Ordinal);
        var nextSection = architecture.IndexOf("\n---", protectedStart, StringComparison.Ordinal);

        Assert.True(groupStart >= 0 && protectedStart > groupStart);
        var group = architecture[groupStart..protectedStart];
        var protectedImport = architecture[protectedStart..nextSection];
        Assert.Contains("/api/sse/stream/invitation-update/{userId}", group, StringComparison.Ordinal);
        Assert.Contains("/api/sse/stream/membership-update/{userId}", group, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/sse/stream/invitations", architecture, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/sse/stream/memberships", architecture, StringComparison.Ordinal);
        Assert.Contains("not authenticated or authorized", group, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/sse/import", group, StringComparison.Ordinal);
        Assert.Contains("do not own", group, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, architecture.Split("/api/sse/import", StringSplitOptions.None).Length - 1);
        Assert.Contains("/api/sse/import", protectedImport, StringComparison.Ordinal);
        Assert.Contains("NameIdentifier", protectedImport, StringComparison.Ordinal);
        Assert.Contains("import-state", protectedImport, StringComparison.Ordinal);
        Assert.Contains("enrichment-state", protectedImport, StringComparison.Ordinal);
        Assert.Contains("relational page reload", protectedImport, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/sse/stream/import-progress", architecture, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/api/sse/stream/location-update/{userName}", group, StringComparison.Ordinal);
        Assert.Contains("/api/sse/stream/job-status", group, StringComparison.Ordinal);
    }

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

    [Fact]
    public void AcceptedLocationHistoryFormatListsNameWayfarerGeoJsonPrecisely()
    {
        var ambiguousLists = new[]
        {
            "GPX/KML/CSV/GeoJSON",
            "JSON, GPX, KML, GeoJSON"
        };
        var files = Directory.GetFiles(RepositoryFile("docs"), "*.md")
            .Append(RepositoryFile("README.md"));

        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var ambiguous in ambiguousLists)
                Assert.DoesNotContain(ambiguous, text, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Wayfarer GeoJSON", File.ReadAllText(RepositoryFile("docs", "01-Getting-Started.md")));
        Assert.Contains("Wayfarer GeoJSON", File.ReadAllText(RepositoryFile("docs", "15-Architecture.md")));
    }

    private static string RepositoryFile(params string[] parts) => Path.GetFullPath(
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts]));
}
