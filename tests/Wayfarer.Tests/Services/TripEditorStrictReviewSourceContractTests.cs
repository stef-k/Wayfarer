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
        Assert.Contains("estimatedDistanceKm", browser, StringComparison.Ordinal);
        Assert.Contains("estimatedDurationMinutes", browser, StringComparison.Ordinal);
        Assert.Contains("estimatedDurationSource", browser, StringComparison.Ordinal);
        Assert.Contains("transportProfileId", browser, StringComparison.Ordinal);
        Assert.Contains("routeCoordinates", browser, StringComparison.Ordinal);
        Assert.Contains("verify-preserved", browser, StringComparison.Ordinal);
        Assert.Contains("provider-reread", fixture, StringComparison.Ordinal);
    }

    /// <summary>The definitive #407 browser entrypoint must own setup and unconditional verified cleanup.</summary>
    [Fact]
    public void WaypointBrowserRunner_IsFinallyProtectedAndRunOwned()
    {
        var runner = Read("tools", "run-407-waypoint-browser.ps1");

        Assert.Contains("finally", runner, StringComparison.Ordinal);
        Assert.Contains("verify-cleanup", runner, StringComparison.Ordinal);
        Assert.Contains("--workers=1", runner, StringComparison.Ordinal);
        Assert.Contains("--retries=0", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-Port $databasePort $false", runner, StringComparison.Ordinal);
        Assert.Contains("Wait-Port $hostPort $false", runner, StringComparison.Ordinal);
        Assert.Contains("Remove-Item -LiteralPath $resolvedRunRoot", runner, StringComparison.Ordinal);
        Assert.Contains("Browser execution and cleanup both failed", runner, StringComparison.Ordinal);
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
