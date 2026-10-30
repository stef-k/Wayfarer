using Wayfarer.Models;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Services;

/// <summary>Restores server-owned Web presentation; protected mutation commands own all editing effects.</summary>
public static class LocationManualAddressEdit
{
    /// <summary>Restores fields that must come from the currently owned database snapshot.</summary>
    public static void RestoreServerFields(AddLocationViewModel model, Location location)
    {
        // These fields are presentation only; the same-snapshot mutation evidence owns comparison authority.
        model.ProviderAddressLine1 = location.ProviderAddressLine1;
        model.IsGeoapifyAddress = LocationProviders.ResolvedFeatureMetadata.NormalizePersisted(null, null,
            location.ReverseGeocodingProvider, location.ReverseGeocodingStorageMode, location.ReverseGeocodedAt).Provider == "geoapify";
        model.ResolvedFeatureName = location.ResolvedFeatureName;
        model.ResolvedFeatureType = location.ResolvedFeatureType;
        model.Source = location.Source;
        model.IsUserInvoked = location.IsUserInvoked;
        model.Provider = location.Provider;
        model.Bearing = location.Bearing;
        model.AppVersion = location.AppVersion;
        model.AppBuild = location.AppBuild;
        model.DeviceModel = location.DeviceModel;
        model.OsVersion = location.OsVersion;
        model.BatteryLevel = location.BatteryLevel;
        model.IsCharging = location.IsCharging;
    }

}
