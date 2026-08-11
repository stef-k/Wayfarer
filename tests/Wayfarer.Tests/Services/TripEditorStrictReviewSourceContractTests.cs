using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Locks the narrow production ownership boundaries required by the #407 strict review.</summary>
public sealed class TripEditorStrictReviewSourceContractTests
{
    /// <summary>The editor create path must use its own relational orchestration rather than the public reconciler wrapper.</summary>
    [Fact]
    public void EditorCreate_DoesNotDelegateToTransactionOwningReconciler()
    {
        var source = Read("Services", "TripEditorSegmentMutationService.cs");

        Assert.DoesNotContain("SegmentRouteReconciler.CreateAsync(_dbContext", source, StringComparison.Ordinal);
        Assert.Contains("CreateRelationalAsync", source, StringComparison.Ordinal);
    }

    /// <summary>The relational notes writer must own an atomic notes-and-Trip-timestamp transaction.</summary>
    [Fact]
    public void NotesOnlyRelationalWriter_UpdatesTripTimestampInsideOwnedTransaction()
    {
        var action = Read("Services", "SegmentNotesMutation.cs");

        Assert.Contains("BeginTransactionAsync", action, StringComparison.Ordinal);
        Assert.Contains("context.Trips", action, StringComparison.Ordinal);
        Assert.Contains("UpdatedAt", action, StringComparison.Ordinal);
        Assert.Contains("CancellationToken.None", action, StringComparison.Ordinal);
    }

    /// <summary>The browser fixture and reread must name every independently seeded measurement and provenance value.</summary>
    [Fact]
    public void WaypointBrowserEvidence_ContainsCompleteMeasurementAndProviderRereadAssertions()
    {
        var fixture = Read("tools", "Wayfarer.WaypointBrowserFixture", "Program.cs");
        var browser = Read("tests", "e2e", "trip-editor", "tripEditorWaypointAggregateContracts.spec.ts");

        Assert.Contains("EstimatedDistanceKm", fixture, StringComparison.Ordinal);
        Assert.Contains("EstimatedDurationSource", fixture, StringComparison.Ordinal);
        Assert.DoesNotContain("provider-reread", browser, StringComparison.Ordinal);
        Assert.Fail("Strict-review red checkpoint: browser/provider reread omitted complete measurement and provenance assertions.");
    }

    /// <summary>The definitive #407 browser entrypoint must own setup and unconditional verified cleanup.</summary>
    [Fact]
    public void WaypointBrowserRunner_IsFinallyProtectedAndRunOwned()
    {
        var scripts = Directory.Exists(Path.Combine(FindRepositoryRoot(), "tools"))
            ? Directory.GetFiles(Path.Combine(FindRepositoryRoot(), "tools"), "*407*", SearchOption.TopDirectoryOnly)
            : [];

        Assert.Empty(scripts);
        Assert.Fail("Strict-review red checkpoint: #407 browser execution lacked finally-protected orchestration.");
    }

    private static string Read(params string[] path) => File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), Path.Combine(path)));

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Wayfarer.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Wayfarer repository root was not found.");
    }
}
