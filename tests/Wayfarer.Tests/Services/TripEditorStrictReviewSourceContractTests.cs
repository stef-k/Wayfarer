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

    /// <summary>The canonical browser distance must be derived independently from literal fixture geometry.</summary>
    [Fact]
    public void WaypointBrowserFixture_UsesIndependentLiteralDistanceEvidence()
    {
        var fixture = Read("tools", "Wayfarer.WaypointBrowserFixture", "Program.cs");

        Assert.Contains("const double earthRadiusMetres = 6_371_000d;", fixture, StringComparison.Ordinal);
        Assert.Contains("MidpointRounding.AwayFromZero", fixture, StringComparison.Ordinal);
        Assert.Contains("AssertCanonicalDistance", fixture, StringComparison.Ordinal);
        Assert.Contains("8.303", fixture, StringComparison.Ordinal);
        Assert.Contains("9.407", fixture, StringComparison.Ordinal);
    }

    /// <summary>Create recovery must compare materially relevant provider state through a complete immutable projection.</summary>
    [Fact]
    public void CreateRecovery_UsesCompleteProviderSnapshot()
    {
        var tests = Read("tests", "Wayfarer.Tests", "Services", "TripEditorSegmentCreatePostgresTests.cs");

        Assert.Contains("CreateRecoveryProviderSnapshot", tests, StringComparison.Ordinal);
        Assert.Contains("RouteCoordinates", tests, StringComparison.Ordinal);
        Assert.Contains("WaypointSnapshots", tests, StringComparison.Ordinal);
        Assert.Contains("RowVersion", tests, StringComparison.Ordinal);
        Assert.Contains("LocationSrid", tests, StringComparison.Ordinal);
    }

    /// <summary>The restoration aggregate must retain the exact injected operation exception object.</summary>
    [Fact]
    public void RestorationFailure_AssertsOriginalOperationIdentity()
    {
        var tests = Read("tests", "Wayfarer.Tests", "Services", "TripEditorSegmentRecoveryPostgresTests.cs");

        Assert.Equal(2, Count(tests, "original => Assert.Same(operation.Failure, original)"));
    }

    /// <summary>Cleanup verification must be independent and failed-run evidence must never be deleted.</summary>
    [Fact]
    public void WaypointBrowserRunner_VerifiesCleanupSeparatelyAndRetainsFailureEvidence()
    {
        var runner = Read("tools", "run-407-waypoint-browser.ps1");

        Assert.Contains("cleanupVerificationAttempted", runner, StringComparison.Ordinal);
        Assert.Contains("Retained evidence directory:", runner, StringComparison.Ordinal);
        Assert.Contains("if (!$originalFailure -and $cleanupFailures.Count -eq 0)", runner, StringComparison.Ordinal);
    }

    private static string Read(params string[] path) => File.ReadAllText(
        Path.Combine(FindRepositoryRoot(), Path.Combine(path)));

    private static int Count(string source, string value) =>
        source.Split(value, StringSplitOptions.None).Length - 1;

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Wayfarer.csproj")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Wayfarer repository root was not found.");
    }
}
