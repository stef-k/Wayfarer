using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Defines which provider outcomes short-circuit only enrichment for an import execution.</summary>
public sealed class LocationImportEnrichmentHandoffTests
{
    [Theory]
    [InlineData(ReverseGeocodingCategory.Exhausted)]
    [InlineData(ReverseGeocodingCategory.NoProviderSelected)]
    [InlineData(ReverseGeocodingCategory.CredentialRequired)]
    [InlineData(ReverseGeocodingCategory.Unauthorized)]
    [InlineData(ReverseGeocodingCategory.VerificationRequired)]
    [InlineData(ReverseGeocodingCategory.StaleAuthority)]
    public void RunWideNoContactOutcomesDisableRemainingInlineAttempts(ReverseGeocodingCategory category)
        => Assert.True(LocationImportService.IsRunWideNoContact(category));

    [Theory]
    [InlineData(ReverseGeocodingCategory.InvalidRequest)]
    [InlineData(ReverseGeocodingCategory.InvalidResponse)]
    public void RecordSpecificOutcomesDoNotDisableRemainingInlineAttempts(ReverseGeocodingCategory category)
        => Assert.False(LocationImportService.IsRunWideNoContact(category));
}
