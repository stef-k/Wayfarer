using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Services;
using Wayfarer.Services.LocationProviders;
using Wayfarer.Util;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>Ordinary Web forms adapt owner-bound creation to the shared first-winner persistence owner.</summary>
public partial class LocationController
{
    private IDataProtector CreateProtector => HttpContext.RequestServices.GetRequiredService<IDataProtectionProvider>()
        .CreateProtector("Wayfarer.WebLocationCreate.1");

    /// <summary>A deliberate new form allocates one identity before its first submission.</summary>
    [HttpGet, ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
    public async Task<IActionResult> Create()
    {
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (owner == null) return Unauthorized();
        var key = Guid.NewGuid();
        var model = new AddLocationViewModel { UserId = owner, CreateKey = key,
            CreateIntent = CreateProtector.Protect($"{owner}:{key:D}"), LocalTimestamp = DateTime.Now };
        return await CreateFormAsync(model);
    }

    /// <summary>Retries original attempted values while redisplaying newer input as unsaved, never as an update.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(AddLocationViewModel model)
    {
        var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (owner == null) return Unauthorized();
        try
        {
            if (model.CreateKey == null || CreateProtector.Unprotect(model.CreateIntent ?? "") != $"{owner}:{model.CreateKey:D}") return Forbid();
        }
        catch (CryptographicException) { return Forbid(); }
        model.UserId = owner;
        ModelState.Remove(nameof(model.ActivityTypes));
        // An observed winner/deletion takes priority over validation of newer unsaved form input.
        // Absence is never used to settle an earlier possibly effective attempt.
        if (await LocationDeletionFence.IsDeletedAsync(_dbContext, owner, model.CreateKey, HttpContext.RequestAborted))
        {
            model.CreateTerminal = true;
            model.CreateStatus = "This location was deleted. This retry cannot restore it. Your input remains available.";
            return await CreateFormAsync(model);
        }
        var existing = await _dbContext.Locations.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == owner && row.IdempotencyKey == model.CreateKey);
        if (existing != null) return await ShowCreateWinnerAsync(model, existing, false);

        WebLocationCreateValues attempt;
        try
        {
            attempt = string.IsNullOrEmpty(model.AttemptedValues) ? WebLocationCreateValues.From(model)
                : JsonSerializer.Deserialize<WebLocationCreateValues>(model.AttemptedValues, WebLocationCreateValues.Json)!;
            if (attempt == null || !attempt.IsValid() || !model.CreateMayHaveEffect && !ModelState.IsValid)
                return await RejectCreateAsync(model);
        }
        catch (JsonException) { return await RejectCreateAsync(model); }

        // Retain these before any possibly effective call, including errors after an uncertain commit.
        model.AttemptedValues = JsonSerializer.Serialize(attempt, WebLocationCreateValues.Json);
        model.CreateMayHaveEffect = true;
        ModelState.Remove(nameof(model.AttemptedValues));
        ModelState.Remove(nameof(model.CreateMayHaveEffect));
        try
        {
            var location = await WebCreateLocationAsync(owner, model.CreateKey!.Value, attempt);
            var persisted = await ApiLocationCreation.PersistAsync(_dbContext, _reverseGeocodingService, location,
                HttpContext.RequestAborted, enrich: !HasManualAddress(attempt));
            // Read the actual current winner, including intervening authorized edits.
            var winner = await _dbContext.Locations.AsNoTracking().SingleAsync(row => row.UserId == owner && row.Id == persisted.Location.Id);
            return await ShowCreateWinnerAsync(model, winner, persisted.Created);
        }
        catch (LocationDeletedException)
        {
            model.CreateTerminal = true;
            model.CreateStatus = "This location was deleted. This retry cannot restore it. Your input remains available.";
        }
        catch (Exception error)
        {
            _logger.LogWarning("Web location creation outcome requires recovery. Failure type: {FailureType}", error.GetType().Name);
            // A provider/publication failure after commit cannot relabel the confirmed creation as failed.
            try
            {
                if (await LocationDeletionFence.IsDeletedAsync(_dbContext, owner, model.CreateKey, HttpContext.RequestAborted))
                {
                    model.CreateTerminal = true;
                    model.CreateStatus = "This location was deleted. This retry cannot restore it. Your input remains available.";
                }
                else
                {
                    var winner = await _dbContext.Locations.AsNoTracking().SingleOrDefaultAsync(row => row.UserId == owner && row.IdempotencyKey == model.CreateKey);
                    if (winner != null) return await ShowCreateWinnerAsync(model, winner, false);
                }
            }
            catch (Exception recoveryError)
            { _logger.LogWarning("Web location recovery is unconfirmed. Failure type: {FailureType}", recoveryError.GetType().Name); }
            model.CreateStatus ??= "The save could not be confirmed. Retry uses the original attempted values and UUID. Current form changes remain unsaved.";
        }
        return await CreateFormAsync(model);
    }

    /// <summary>A definite first-attempt rejection permits corrections under the same identity.</summary>
    private async Task<IActionResult> RejectCreateAsync(AddLocationViewModel model)
    {
        if (model.CreateMayHaveEffect)
            model.CreateStatus = "The earlier save is still unconfirmed. Keep this form and retry the original attempt.";
        else
        {
            model.AttemptedValues = null;
            ModelState.Remove(nameof(model.AttemptedValues));
            model.CreateStatus = "Correct the field errors. This submission did not create a location.";
        }
        ModelState.AddModelError("", model.CreateStatus);
        return await CreateFormAsync(model);
    }

    /// <summary>Source-specific Web fields remain explicit; manual history receives no device, fix or live facts.</summary>
    private async Task<Location> WebCreateLocationAsync(string owner, Guid key, WebLocationCreateValues values)
    {
        static string? Manual(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new Location
        {
            UserId = owner, IdempotencyKey = key, Timestamp = ObservationEvidence.Truncate(DateTime.UtcNow),
            LocalTimestamp = CoordinateTimeZoneConverter.ConvertToUtc(values.Latitude, values.Longitude, values.LocalTimestamp),
            TimeZoneId = CoordinateTimeZoneConverter.GetTimeZoneIdFromCoordinates(values.Latitude, values.Longitude),
            Coordinates = new(values.Longitude, values.Latitude) { SRID = 4326 },
            ActivityTypeId = await new ActivityCatalog(_dbContext).ResolveWriteIdAsync(values.SelectedActivityId, HttpContext.RequestAborted),
            Notes = RichNotes.Normalize(values.Notes), Accuracy = values.Accuracy, Altitude = values.Altitude, Speed = values.Speed,
            Address = Manual(values.Address), FullAddress = Manual(values.FullAddress), AddressNumber = Manual(values.AddressNumber),
            StreetName = Manual(values.StreetName), PostCode = Manual(values.PostCode), Place = Manual(values.Place),
            Region = Manual(values.Region), Country = Manual(values.Country), Source = "web-create", IsUserInvoked = true,
            CoordinateOrigin = "manual-map"
        };
    }

    private static bool HasManualAddress(WebLocationCreateValues values) => new[] { values.Address, values.FullAddress,
        values.AddressNumber, values.StreetName, values.PostCode, values.Place, values.Region, values.Country }.Any(value => !string.IsNullOrWhiteSpace(value));

    /// <summary>Compares current authored values under creation normalization, then offers fresh protected Edit authority.</summary>
    private async Task<IActionResult> ShowCreateWinnerAsync(AddLocationViewModel model, Location winner, bool created)
    {
        model.SavedLocationId = winner.Id;
        model.CreateTerminal = true;
        var matches = false;
        if (ModelState.IsValid && WebLocationCreateValues.From(model).IsValid())
        {
            try
            {
                var authored = WebLocationCreateValues.From(model);
                var current = await WebCreateLocationAsync(model.UserId!, model.CreateKey!.Value, authored);
                // Blank input can ignore provider-derived content only when the original attempt also had no manual intent.
                var attemptedManualAddress = true;
                try
                {
                    var original = JsonSerializer.Deserialize<WebLocationCreateValues>(model.AttemptedValues ?? "null", WebLocationCreateValues.Json);
                    attemptedManualAddress = original == null || HasManualAddress(original);
                }
                catch (JsonException) { /* Missing comparison evidence cannot certify that current input was applied. */ }
                var providerAddress = ResolvedFeatureMetadata.NormalizePersisted(null, null,
                    winner.ReverseGeocodingProvider, winner.ReverseGeocodingStorageMode, winner.ReverseGeocodedAt).Provider != null;
                var compareAddress = HasManualAddress(authored) || attemptedManualAddress || !providerAddress;
                matches = current.Coordinates.EqualsExact(winner.Coordinates) && ObservationEvidence.Truncate(current.LocalTimestamp) == winner.LocalTimestamp
                && current.ActivityTypeId == winner.ActivityTypeId && current.Notes == RichNotes.Normalize(winner.Notes)
                && current.Altitude == winner.Altitude && current.Accuracy == winner.Accuracy && current.Speed == winner.Speed
                && (!compareAddress || current.Address == winner.Address && current.FullAddress == winner.FullAddress
                    && current.AddressNumber == winner.AddressNumber && current.StreetName == winner.StreetName && current.PostCode == winner.PostCode
                    && current.Place == winner.Place && current.Region == winner.Region && current.Country == winner.Country);
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            { _logger.LogWarning("Saved location found; current input could not be compared. Failure type: {FailureType}", error.GetType().Name); }
        }
        model.CreateStatus = matches ? created ? "The location was saved." : "The original creation was found. No additional record was created."
            : "This location was already created. Your current form changes were not applied and remain unsaved. Open the saved location and use Edit to apply differences.";
        ViewData["CreateWinner"] = winner.ForPublication();
        if (created)
        {
            try
            {
                var userName = await _dbContext.Users.Where(row => row.Id == model.UserId).Select(row => row.UserName).SingleAsync();
                await _sse.BroadcastAsync($"location-update-{userName ?? model.UserId}", JsonSerializer.Serialize(new { LocationId = winner.Id, TimeStamp = winner.Timestamp, isLive = false }));
            }
            catch (Exception error) { _logger.LogWarning("Location saved; optional notification unavailable. Failure type: {FailureType}", error.GetType().Name); }
        }
        return await CreateFormAsync(model);
    }

    /// <summary>Ordinary redisplay retains field values, validation and discoverable outcome information.</summary>
    private async Task<IActionResult> CreateFormAsync(AddLocationViewModel model)
    {
        model.ActivityTypes = (await _dbContext.ActivityTypes.ToListAsync()).Select(activity => new SelectListItem(activity.Name,
            activity.Id.ToString(), activity.Id == model.SelectedActivityId)).ToList();
        model.Notes = RichNotes.Normalize(model.Notes);
        SetPageTitle("Add Location");
        return View("Create", model);
    }
}
