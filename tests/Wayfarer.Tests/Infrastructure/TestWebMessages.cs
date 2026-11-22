using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Localization;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Supplies the actual bundled catalogs to controller tests that construct the action directly.</summary>
internal static class TestWebMessages
{
    /// <summary>Uses the ambient test UI culture with normal English fallback.</summary>
    internal static readonly WebMessages Instance = new(WebLocalization.ReadLocales(new ConfigurationBuilder().Build()),
        NullLogger<WebMessages>.Instance);
}
