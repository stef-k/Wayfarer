using Xunit;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Proves fixture cleanup remains exhaustive and preserves the primary failure.</summary>
public sealed class FailureIndependentCleanupTests
{
    [Fact]
    public async Task PrimaryAndMultipleCleanupFailuresAreJointlyReportedAfterEveryStepRuns()
    {
        var actions = new List<string>();
        var primary = new InvalidOperationException("primary");

        var failure = await Assert.ThrowsAsync<AggregateException>(() => FailureIndependentCleanup.CompleteAsync(
            primary,
            [
                Step("shutdown-first", actions, new ApplicationException("shutdown")),
                Step("dispose-first", actions),
                Step("relational", actions, new ApplicationException("relational")),
                Step("quartz", actions),
                Step("schema", actions, new ApplicationException("schema"))
            ]));

        Assert.Equal(["shutdown-first", "dispose-first", "relational", "quartz", "schema"], actions);
        Assert.Same(primary, failure.InnerExceptions[0]);
        Assert.Equal(4, failure.InnerExceptions.Count);
        Assert.Contains(failure.InnerExceptions, item => item.Message.Contains("shutdown-first", StringComparison.Ordinal));
        Assert.Contains(failure.InnerExceptions, item => item.Message.Contains("relational", StringComparison.Ordinal));
        Assert.Contains(failure.InnerExceptions, item => item.Message.Contains("schema", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CleanupOnlyFailureReportsAllFailuresAndDoesNotSuppressLaterActions()
    {
        var actions = new List<string>();

        var failure = await Assert.ThrowsAsync<AggregateException>(() => FailureIndependentCleanup.CompleteAsync(
            null,
            [Step("one", actions, new ApplicationException("one")), Step("two", actions),
                Step("three", actions, new ApplicationException("three"))]));

        Assert.Equal(["one", "two", "three"], actions);
        Assert.Equal(2, failure.InnerExceptions.Count);
    }

    [Fact]
    public async Task PrimaryIsRethrownUnchangedWhenCleanupSucceeds()
    {
        var primary = new InvalidOperationException("primary");
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            FailureIndependentCleanup.CompleteAsync(primary, []));
        Assert.Same(primary, thrown);
    }

    private static (string Name, Func<Task> Action) Step(
        string name, ICollection<string> actions, Exception? failure = null) =>
        (name, () =>
        {
            actions.Add(name);
            return failure is null ? Task.CompletedTask : Task.FromException(failure);
        });
}
