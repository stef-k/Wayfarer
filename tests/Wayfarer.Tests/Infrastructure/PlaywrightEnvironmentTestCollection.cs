using Xunit;
using Xunit.Sdk;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>
/// Serializes tests that read or mutate Playwright's process-wide browser discovery path.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PlaywrightEnvironmentTestCollection
{
    /// <summary>Stable collection name for process-wide Playwright environment users.</summary>
    public const string Name = "Playwright browser environment";
}

/// <summary>
/// Restores one process-wide environment variable to its exact original state after a test.
/// </summary>
internal sealed class PlaywrightEnvironmentIsolationAttribute : BeforeAfterTestAttribute
{
    private string? _originalValue;

    /// <summary>Snapshots the browser path before each affected test executes.</summary>
    public override void Before(System.Reflection.MethodInfo methodUnderTest) =>
        _originalValue = Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH");

    /// <summary>Restores the exact original browser path after success or failure.</summary>
    public override void After(System.Reflection.MethodInfo methodUnderTest) =>
        Environment.SetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH", _originalValue);
}
