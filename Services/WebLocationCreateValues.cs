using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Services;

/// <summary>Only Web-authored creation values; retained form attempts grant no owner or mutation authority.</summary>
internal sealed record WebLocationCreateValues(double Latitude, double Longitude, DateTime LocalTimestamp,
    int? SelectedActivityId, double? Accuracy, double? Altitude, double? Speed, string? Notes, string? Address,
    string? FullAddress, string? AddressNumber, string? StreetName, string? PostCode, string? Place, string? Region, string? Country)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Freezes permitted authored values independently of presentation fields and current input.</summary>
    internal static WebLocationCreateValues From(AddLocationViewModel model) => new(model.Latitude, model.Longitude,
        model.LocalTimestamp, model.SelectedActivityId, model.Accuracy, model.Altitude, model.Speed, model.Notes,
        model.Address, model.FullAddress, model.AddressNumber, model.StreetName, model.PostCode, model.Place, model.Region, model.Country);

    /// <summary>Reuses form validation for a previously attempted payload without validating newer, unsaved input.</summary>
    internal bool IsValid()
    {
        var model = new AddLocationViewModel { Latitude = Latitude, Longitude = Longitude, LocalTimestamp = LocalTimestamp,
            Address = Address, FullAddress = FullAddress, AddressNumber = AddressNumber, StreetName = StreetName,
            PostCode = PostCode, Place = Place, Region = Region, Country = Country };
        return Validator.TryValidateObject(model, new ValidationContext(model), [], true)
            && double.IsFinite(Latitude) && double.IsFinite(Longitude) && LocalTimestamp != default
            && new[] { Accuracy, Altitude, Speed }.All(value => value == null || double.IsFinite(value.Value));
    }
}
