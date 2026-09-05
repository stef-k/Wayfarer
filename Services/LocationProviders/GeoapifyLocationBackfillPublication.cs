using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Services.LocationProviders;

/// <summary>Atomic address publication; transaction and fencing ownership remain in the backfill workflow.</summary>
public sealed partial class GeoapifyLocationBackfillService
{
    /// <summary>Initial enrichment publishes all fields; repair only fills missing retained values.</summary>
    private static async Task<bool> PublishAddressAsync(IQueryable<Location> eligibleLocations, ReverseLocationResults value,
        string provider, DateTimeOffset persistedAt, bool incompleteRepair, CancellationToken cancellationToken)
    {
        if (incompleteRepair)
        {
            return await IncompleteGeoapify(eligibleLocations).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.FullAddress, item => item.FullAddress == null || item.FullAddress.Trim() == ""
                    ? value.FullAddress : item.FullAddress)
                .SetProperty(item => item.Address, item => item.Address == null || item.Address.Trim() == ""
                    ? value.Address : item.Address)
                .SetProperty(item => item.AddressNumber, item => item.AddressNumber == null || item.AddressNumber.Trim() == ""
                    ? value.AddressNumber : item.AddressNumber)
                .SetProperty(item => item.StreetName, item => item.StreetName == null || item.StreetName.Trim() == ""
                    ? value.StreetName : item.StreetName)
                .SetProperty(item => item.PostCode, item => item.PostCode == null || item.PostCode.Trim() == ""
                    ? value.PostCode : item.PostCode)
                .SetProperty(item => item.Place, item => item.Place == null || item.Place.Trim() == ""
                    ? value.Place : item.Place)
                .SetProperty(item => item.Region, item => item.Region == null || item.Region.Trim() == ""
                    ? value.Region : item.Region)
                .SetProperty(item => item.Country, item => item.Country == null || item.Country.Trim() == ""
                    ? value.Country : item.Country)
                .SetProperty(item => item.ProviderAddressLine1, item => item.ProviderAddressLine1 == null || item.ProviderAddressLine1.Trim() == ""
                    ? value.ProviderAddressLine1 : item.ProviderAddressLine1)
                .SetProperty(item => item.ResolvedFeatureName,
                    item => item.ResolvedFeatureName == null || item.ResolvedFeatureName.Trim() == ""
                        ? value.ResolvedFeatureName : item.ResolvedFeatureName)
                .SetProperty(item => item.ResolvedFeatureType,
                    item => item.ResolvedFeatureType == null || item.ResolvedFeatureType.Trim() == ""
                        ? value.ResolvedFeatureType : item.ResolvedFeatureType)
                .SetProperty(item => item.ReverseGeocodedAt, persistedAt), cancellationToken) == 1;
        }
        else
        {
            return await WhollyUnenriched(eligibleLocations).ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.FullAddress, value.FullAddress)
                .SetProperty(item => item.Address, value.Address)
                .SetProperty(item => item.AddressNumber, value.AddressNumber)
                .SetProperty(item => item.StreetName, value.StreetName)
                .SetProperty(item => item.PostCode, value.PostCode)
                .SetProperty(item => item.Place, value.Place)
                .SetProperty(item => item.Region, value.Region)
                .SetProperty(item => item.Country, value.Country)
                .SetProperty(item => item.ProviderAddressLine1, value.ProviderAddressLine1)
                .SetProperty(item => item.ResolvedFeatureName, value.ResolvedFeatureName)
                .SetProperty(item => item.ResolvedFeatureType, value.ResolvedFeatureType)
                .SetProperty(item => item.ReverseGeocodingProvider, provider)
                .SetProperty(item => item.ReverseGeocodingStorageMode,
                    provider == "geoapify" ? "persistent" : "permanent")
                .SetProperty(item => item.ReverseGeocodedAt, persistedAt), cancellationToken) == 1;
        }
    }
}
