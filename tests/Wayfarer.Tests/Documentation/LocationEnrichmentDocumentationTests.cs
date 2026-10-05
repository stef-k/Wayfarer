using Xunit;

namespace Wayfarer.Tests.Documentation;

/// <summary>Prevents canonical enrichment documentation from drifting behind the shipped workflow.</summary>
public sealed class LocationEnrichmentDocumentationTests
{
    /// <summary>Protected import SSE stays authenticated, content-free and separate from the legacy generic route.</summary>
    [Fact]
    public void CanonicalDocsDescribeOnlyAuthenticatedProtectedImportSseRoute()
    {
        var docs = CanonicalDocs("development/architecture.md", "development/api.md");
        var combined = string.Join(Environment.NewLine, docs);

        Assert.DoesNotContain("/api/sse/stream/import-progress", combined, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotMatch(@"/api/sse/stream/(?:[^\s`|)]*(?:import|enrichment)[^\s`|)]*)",
            combined.ToLowerInvariant());
        Assert.Contains("/api/sse/import", combined, StringComparison.Ordinal);
        Assert.Contains("authenticated", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("NameIdentifier", combined, StringComparison.Ordinal);
        Assert.Contains("content-free SSE", combined, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("relational", combined, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonical architecture/API docs keep public, group and protected-import stream domains distinct.</summary>
    [Fact]
    public void ArchitectureSeparatesGroupAndProtectedImportStreamDomains()
    {
        var architecture = File.ReadAllText(RepositoryFile("docs", "development", "architecture.md"));
        var api = File.ReadAllText(RepositoryFile("docs", "development", "api.md"));

        Assert.Contains("/api/sse/stream/location-update/{username}", architecture, StringComparison.Ordinal);
        Assert.Contains("/api/sse/import", architecture, StringComparison.Ordinal);
        Assert.Contains("/api/sse/group-notifications", architecture, StringComparison.Ordinal);
        Assert.Contains("Dedicated protected streams", architecture, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/sse/stream/invitation-update", architecture, StringComparison.Ordinal);
        Assert.DoesNotContain("/api/sse/stream/membership-update", architecture, StringComparison.Ordinal);

        Assert.Contains("/api/sse/import", api, StringComparison.Ordinal);
        Assert.Contains("NameIdentifier", api, StringComparison.Ordinal);
        Assert.Contains("import-state", api, StringComparison.Ordinal);
        Assert.Contains("enrichment-state", api, StringComparison.Ordinal);
        Assert.Contains("authoritative relational state", api, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("/api/sse/stream/import-progress", api, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The canonical user import guide advertises only formats and actions that exist.</summary>
    [Fact]
    public void ImportingGuideDoesNotAdvertiseUnavailableRegenerateAction()
    {
        var guide = File.ReadAllText(RepositoryFile("docs", "user", "import-export.md"));

        Assert.DoesNotContain("**Regenerate**", guide);
        Assert.Contains("Google Timeline JSON", guide);
        Assert.Contains("generic GeoJSON is not supported", guide, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonical provider/API docs retain the documented capacity and content-free privacy boundary.</summary>
    [Fact]
    public void CanonicalDocsStateCapacityAndPrivacy()
    {
        var docs = string.Join('\n', CanonicalDocs(
            "user/location-providers.md",
            "development/api.md"));

        Assert.Contains("2,500", docs);
        Assert.Contains("content-free SSE", docs, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Canonical architecture keeps location import separate from provider enrichment work.</summary>
    [Fact]
    public void CanonicalDocsDescribeLocationImportAsScheduledOnly()
    {
        var docs = File.ReadAllText(RepositoryFile("docs", "development", "architecture.md"));

        Assert.DoesNotContain("Optional reverse geocoding during import", docs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Parse batches → Optional reverse geocoding → DB insert", docs,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("later inline checks", docs, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("200 ms delay", docs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Location import performs no provider credential resolution, provider admission," +
            Environment.NewLine + "reverse-geocoding HTTP, inline enrichment, or per-record enrichment delay.", docs,
            StringComparison.Ordinal);
    }

    /// <summary>Canonical user and architecture docs name the accepted Wayfarer history format precisely.</summary>
    [Fact]
    public void AcceptedLocationHistoryFormatListsNameWayfarerGeoJsonPrecisely()
    {
        var userGuide = File.ReadAllText(RepositoryFile("docs", "user", "import-export.md"));
        var architecture = File.ReadAllText(RepositoryFile("docs", "development", "architecture.md"));

        Assert.Contains("Wayfarer GeoJSON", userGuide);
        Assert.Contains("Generic GeoJSON is not supported", userGuide);
        Assert.Contains("Wayfarer GeoJSON", architecture);
        foreach (var ambiguous in new[] { "GPX/KML/CSV/GeoJSON", "JSON, GPX, KML, GeoJSON" })
        {
            Assert.DoesNotContain(ambiguous, userGuide, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(ambiguous, architecture, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>Reads the requested canonical documentation files from the repository root.</summary>
    private static string[] CanonicalDocs(params string[] relativePaths)
        => relativePaths.Select(path => File.ReadAllText(
            RepositoryFile("docs", .. path.Split('/')))).ToArray();

    /// <summary>Resolves a repository file from the test output directory.</summary>
    private static string RepositoryFile(params string[] parts) => Path.GetFullPath(
        Path.Combine([AppContext.BaseDirectory, "..", "..", "..", "..", "..", .. parts]));
}
