using System.Runtime.ExceptionServices;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Runs every fixture cleanup action and reports it without losing the test failure.</summary>
internal static class FailureIndependentCleanup
{
    /// <summary>Attempts all cleanup actions and combines every failure in deterministic order.</summary>
    internal static async Task CompleteAsync(Exception? primary,
        IEnumerable<(string Name, Func<Task> Action)> steps)
    {
        var cleanupFailures = new List<Exception>();
        foreach (var (name, action) in steps)
        {
            try { await action(); }
            catch (Exception failure)
            {
                cleanupFailures.Add(new InvalidOperationException($"Fixture cleanup step '{name}' failed.", failure));
            }
        }

        if (primary is not null && cleanupFailures.Count > 0)
            throw new AggregateException("The test and fixture cleanup both failed.", [primary, .. cleanupFailures]);
        if (cleanupFailures.Count > 0)
            throw new AggregateException("Fixture cleanup failed.", cleanupFailures);
        if (primary is not null) ExceptionDispatchInfo.Capture(primary).Throw();
    }
}
