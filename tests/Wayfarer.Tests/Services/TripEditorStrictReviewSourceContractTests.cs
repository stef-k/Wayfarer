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
