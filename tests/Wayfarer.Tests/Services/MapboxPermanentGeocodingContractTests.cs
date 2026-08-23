using System.Net;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.LocationProviders;
using Wayfarer.Parsers;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Locks the production contract for retained Mapbox reverse-geocoding output.</summary>
public sealed class MapboxPermanentGeocodingContractTests
{
    [Fact]
    public void Profile_ProvidesVersionedCredentialBoundPermanentConsent()
    {
        var properties = typeof(PersonalLocationProviderProfile).GetProperties().Select(property => property.Name).ToHashSet();

        Assert.Contains("PermanentGeocodingConsentVersion", properties);
        Assert.Contains("PermanentGeocodingConsentedAt", properties);
        Assert.Contains("PermanentGeocodingConsentCredentialGeneration", properties);
    }

    [Fact]
    public void LocationAndPlace_ProvideNullableEnrichmentProvenance()
    {
        AssertNullableProperties<Location>("ReverseGeocodingProvider", "ReverseGeocodingStorageMode", "ReverseGeocodedAt");
        AssertNullableProperties<Place>("AddressEnrichmentProvider", "AddressEnrichmentStorageMode", "AddressEnrichedAt");
    }

    [Fact]
    public async Task PersistedMapboxRequest_UsesPermanentMode()
    {
        var handler = new RecordingHandler();
        var service = new ReverseGeocodingService(
            new HttpClient(handler), NullLogger<BaseApiController>.Instance);

        await service.GetReverseGeocodingDataAsync(10, 20, "secret", "Mapbox");

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("permanent=true", handler.RequestUri!.Query, StringComparison.Ordinal);
    }

    [Fact]
    public void PersistentBoundary_DoesNotAcceptProviderCredentialOrMode()
    {
        var productionMethods = typeof(ReverseGeocodingService).GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(method => method.DeclaringType == typeof(ReverseGeocodingService)).ToArray();

        Assert.Contains(productionMethods, method =>
            method.GetParameters().Any(parameter => parameter.Name == "userId")
            && method.GetParameters().All(parameter => parameter.Name is not ("apiToken" or "provider")));
    }

    [Fact]
    public void Boundary_ExposesBoundedNoContactAndFailureOutcomes()
    {
        var assembly = typeof(ReverseGeocodingService).Assembly;
        var category = assembly.GetType("Wayfarer.Parsers.ReverseGeocodingCategory");

        Assert.NotNull(category);
        var names = Enum.GetNames(category!);
        Assert.Contains("ConsentRequired", names);
        Assert.Contains("NoProviderSelected", names);
        Assert.Contains("Exhausted", names);
        Assert.Contains("ProviderUnavailable", names);
        Assert.Contains("StaleAuthority", names);
    }

    [Fact]
    public void Verification_IsDedicatedAndDoesNotRequireActiveSelection()
    {
        var serviceType = typeof(ReverseGeocodingService);
        var verification = serviceType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .SingleOrDefault(method => method.Name.Contains("VerifyMapboxPermanent", StringComparison.Ordinal));

        Assert.NotNull(verification);
        Assert.Contains(verification!.GetParameters(), parameter => parameter.Name == "userId");
    }

    [Fact]
    public void LegacyMigrationAndDurableCallers_DoNotOwnMapboxSelectionOrTokens()
    {
        var root = FindRepositoryRoot();
        var migration = File.ReadAllText(Path.Combine(root, "Services", "LocationProviders", "LegacyMapboxMigrationService.cs"));
        Assert.DoesNotContain("GeocodingProviderKey = \"mapbox\"", migration, StringComparison.Ordinal);

        var callers = new[]
        {
            Path.Combine("Areas", "Api", "Controllers", "LocationController.cs"),
            Path.Combine("Areas", "User", "Controllers", "LocationController.cs"),
            Path.Combine("Services", "LocationImportService.cs"),
            Path.Combine("Services", "TripEditorPlaceMutationService.cs")
        };
        foreach (var caller in callers)
        {
            var source = File.ReadAllText(Path.Combine(root, caller));
            Assert.DoesNotContain("ApiTokens", source, StringComparison.Ordinal);
            Assert.DoesNotContain("GetReverseGeocodingDataAsync", source, StringComparison.Ordinal);
        }
    }

    private static void AssertNullableProperties<T>(params string[] names)
    {
        foreach (var name in names)
        {
            var property = typeof(T).GetProperty(name);
            Assert.NotNull(property);
            Assert.True(!property!.PropertyType.IsValueType || Nullable.GetUnderlyingType(property.PropertyType) != null);
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "Wayfarer.csproj"))) directory = directory.Parent;
        return Assert.IsType<DirectoryInfo>(directory).FullName;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"type\":\"FeatureCollection\",\"features\":[]}")
            });
        }
    }
}
