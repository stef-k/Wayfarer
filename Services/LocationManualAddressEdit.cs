using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc.Rendering;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Services;

/// <summary>Applies normalized manual address edits without retaining provider provenance.</summary>
public static class LocationManualAddressEdit
{
    /// <summary>Reloads authoritative display fields and choices while retaining submitted edit values.</summary>
    public static async Task<bool> ReloadInvalidAsync(ApplicationDbContext db, AddLocationViewModel model,
        string userId, CancellationToken cancellationToken)
    {
        var location = await db.Locations.AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == model.Id && item.UserId == userId, cancellationToken);
        if (location == null) return false;
        RestoreServerFields(model, location);
        model.ActivityTypes = await db.ActivityTypes.AsNoTracking().Select(activity => new SelectListItem
        {
            Value = activity.Id.ToString(),
            Text = activity.Name,
            Selected = activity.Id == model.SelectedActivityId
        }).ToListAsync(cancellationToken);
        return true;
    }

    /// <summary>Restores fields that must come from the currently owned database row after an invalid post.</summary>
    public static void RestoreServerFields(AddLocationViewModel model, Location location)
    {
        // Submitted address values retain their original publication authority, including on redisplay.
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

    /// <summary>Loads and locks the owned Location for the short manual-edit transaction.</summary>
    public static Task<Location?> LockOwnedAsync(ApplicationDbContext db, int? id, string userId,
        CancellationToken cancellationToken) => db.Database.IsNpgsql()
        ? db.Locations.FromSqlInterpolated($$"""
            SELECT * FROM "Locations" WHERE "Id" = {{id}} AND "UserId" = {{userId}} FOR UPDATE
            """).SingleOrDefaultAsync(cancellationToken)
        : db.Locations.SingleOrDefaultAsync(item => item.Id == id && item.UserId == userId, cancellationToken);

    /// <summary>Returns whether the form was loaded from the Location's current provider publication.</summary>
    public static bool HasCurrentProviderTuple(Location location, AddLocationViewModel model) =>
        string.Equals(location.ReverseGeocodingProvider, Normalize(model.OriginalReverseGeocodingProvider),
            StringComparison.Ordinal)
        && string.Equals(location.ReverseGeocodingStorageMode,
            Normalize(model.OriginalReverseGeocodingStorageMode), StringComparison.Ordinal)
        && location.ReverseGeocodedAt == model.OriginalReverseGeocodedAt;

    /// <summary>Applies address values and clears provider metadata according to coordinate-change semantics.</summary>
    public static void Apply(Location location, AddLocationViewModel model, bool coordinatesChanged)
    {
        var current = AddressValues.From(location);
        var submitted = AddressValues.From(model);
        var addressChanged = current != submitted;
        if (!coordinatesChanged && !addressChanged) return;

        var applied = coordinatesChanged ? submitted.OnlyChangedFrom(current) : submitted;
        applied.ApplyTo(location);
        location.ReverseGeocodingProvider = null;
        location.ReverseGeocodingStorageMode = null;
        location.ReverseGeocodedAt = null;
        location.ProviderAddressLine1 = null;
        location.ResolvedFeatureName = null;
        location.ResolvedFeatureType = null;
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private sealed record AddressValues(string? FullAddress, string? Address, string? StreetName,
        string? AddressNumber, string? PostCode, string? Place, string? Region, string? Country)
    {
        public static AddressValues From(Location location) => new(Normalize(location.FullAddress),
            Normalize(location.Address), Normalize(location.StreetName), Normalize(location.AddressNumber),
            Normalize(location.PostCode), Normalize(location.Place), Normalize(location.Region),
            Normalize(location.Country));

        public static AddressValues From(AddLocationViewModel model) => new(Normalize(model.FullAddress),
            Normalize(model.Address), Normalize(model.StreetName), Normalize(model.AddressNumber),
            Normalize(model.PostCode), Normalize(model.Place), Normalize(model.Region), Normalize(model.Country));

        public AddressValues OnlyChangedFrom(AddressValues current) => new(
            FullAddress != current.FullAddress ? FullAddress : null,
            Address != current.Address ? Address : null,
            StreetName != current.StreetName ? StreetName : null,
            AddressNumber != current.AddressNumber ? AddressNumber : null,
            PostCode != current.PostCode ? PostCode : null,
            Place != current.Place ? Place : null,
            Region != current.Region ? Region : null,
            Country != current.Country ? Country : null);

        public void ApplyTo(Location location)
        {
            location.FullAddress = FullAddress;
            location.Address = Address;
            location.StreetName = StreetName;
            location.AddressNumber = AddressNumber;
            location.PostCode = PostCode;
            location.Place = Place;
            location.Region = Region;
            location.Country = Country;
        }
    }
}
