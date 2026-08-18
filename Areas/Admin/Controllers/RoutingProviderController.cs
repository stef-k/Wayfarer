using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Areas.Admin.Models;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;

namespace Wayfarer.Areas.Admin.Controllers;

/// <summary>Orchestrates the focused OSRM administration surface.</summary>
[Area("Admin")]
[Authorize(Roles = "Admin")]
public sealed class RoutingProviderController : Controller
{
    private readonly ApplicationDbContext _dbContext;
    private readonly RoutingProviderAdministrationService _administration;
    private readonly IRoutingProviderVerifier _verifier;
    private readonly RoutingProviderActivationService _activation;

    /// <summary>Initializes thin provider administration orchestration.</summary>
    public RoutingProviderController(
        ApplicationDbContext dbContext, RoutingProviderAdministrationService administration,
        IRoutingProviderVerifier verifier, RoutingProviderActivationService activation)
        => (_dbContext, _administration, _verifier, _activation) = (dbContext, administration, verifier, activation);

    /// <summary>Lists safe provider state and the global feature switch.</summary>
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var settings = await _dbContext.ApplicationSettings.AsNoTracking().SingleAsync(item => item.Id == 1, cancellationToken);
        var providers = await _dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .Include(item => item.ProfileMappings).OrderBy(item => item.DisplayName).ToArrayAsync(cancellationToken);
        return View(new RoutingProviderIndexViewModel(settings.ExternalRouteGenerationEnabled, settings.RowVersion,
            settings.ActiveRoutingProviderConfigurationId, providers.Select(provider => new RoutingProviderRowViewModel(
                provider.Id, provider.DisplayName, RoutingProviderStateResolver.Resolve(provider,
                    settings.ActiveRoutingProviderConfigurationId == provider.Id), provider.Enabled, provider.CredentialPresent,
                provider.ConfigurationVersion, provider.VerifiedConfigurationVersion, provider.RowVersion)).ToArray()));
    }

    /// <summary>Displays a new typed OSRM configuration.</summary>
    public async Task<IActionResult> Create(CancellationToken cancellationToken) =>
        View(await PopulateMappingsAsync(new RoutingProviderEditViewModel(), cancellationToken));

    /// <summary>Creates one allowlisted OSRM configuration.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(RoutingProviderEditViewModel model, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return View(await PopulateMappingsAsync(model, cancellationToken));
        var result = await _administration.SaveAsync(model, AdministratorId(), cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return View(await PopulateMappingsAsync(model, cancellationToken));
        }
        Success("Routing provider created. Verify it before activation.");
        return RedirectToAction(nameof(Edit), new { id = result.ProviderId });
    }

    /// <summary>Displays safe mutable fields and masked credential presence.</summary>
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var provider = await _dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .Include(item => item.ProfileMappings).SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        return provider == null ? NotFound() : View(await PopulateMappingsAsync(ToModel(provider), cancellationToken));
    }

    /// <summary>Updates allowlisted fields; a blank credential preserves ciphertext.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, RoutingProviderEditViewModel model, CancellationToken cancellationToken)
    {
        if (id != model.Id) return NotFound();
        if (!ModelState.IsValid) return View(await PopulateMappingsAsync(model, cancellationToken));
        var result = await _administration.SaveAsync(model, AdministratorId(), cancellationToken);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return View(await PopulateMappingsAsync(model, cancellationToken));
        }
        Success("Routing provider updated; relevant changes require verification.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>Runs bounded verification for the submitted immutable version.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(Guid id, int configurationVersion, uint rowVersion, CancellationToken cancellationToken)
    {
        var result = await _verifier.VerifyAsync(id, configurationVersion, rowVersion, cancellationToken);
        SetResult(result.Succeeded, result.Succeeded ? "Provider verification succeeded." : "Provider verification failed safely.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Verifies then atomically selects the candidate in singleton settings.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Activate(
        Guid id, int configurationVersion, uint providerRowVersion, uint settingsRowVersion, CancellationToken cancellationToken)
    {
        var result = await _activation.VerifyAndActivateAsync(
            id, configurationVersion, providerRowVersion, settingsRowVersion, cancellationToken);
        SetResult(result.Succeeded, result.Succeeded ? "Routing provider activated." : "Provider activation failed; the previous selection was retained.");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Explicitly clears a credential, optionally disabling routing in the same save.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearCredential(
        Guid id, bool confirmed, bool disableRouting, CancellationToken cancellationToken)
    {
        var result = await _administration.ClearCredentialAsync(
            id, confirmed, disableRouting, AdministratorId(), cancellationToken);
        SetResult(result.Succeeded, result.Succeeded ? "Credential cleared." : result.Error!);
        return RedirectToAction(nameof(Edit), new { id });
    }

    /// <summary>Enables or disables the server-authoritative feature state.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> SetFeature(bool enabled, uint settingsRowVersion, CancellationToken cancellationToken)
    {
        var result = await _administration.SetFeatureEnabledAsync(
            enabled, settingsRowVersion, AdministratorId(), cancellationToken);
        SetResult(result.Succeeded, result.Succeeded ? $"External route generation {(enabled ? "enabled" : "disabled")}." : result.Error!);
        return RedirectToAction(nameof(Index));
    }

    private async Task<RoutingProviderEditViewModel> PopulateMappingsAsync(
        RoutingProviderEditViewModel model, CancellationToken cancellationToken)
    {
        var submitted = model.Mappings.ToDictionary(item => item.TransportProfileId, item => item.OsrmProfile);
        model.Mappings = await _dbContext.Set<TransportProfile>().AsNoTracking().Where(item => item.IsActive)
            .OrderBy(item => item.SortOrder).ThenBy(item => item.Label)
            .Select(item => new RoutingProviderMappingViewModel
            {
                TransportProfileId = item.Id, TransportProfileLabel = item.Label,
                OsrmProfile = submitted.GetValueOrDefault(item.Id)
            }).ToListAsync(cancellationToken);
        return model;
    }

    private static RoutingProviderEditViewModel ToModel(RoutingProviderConfiguration provider) => new()
    {
        Id = provider.Id, DisplayName = provider.DisplayName, BaseEndpoint = provider.BaseEndpoint ?? string.Empty,
        CredentialRequired = provider.CredentialRequired, CredentialPresent = provider.CredentialPresent,
        Enabled = provider.Enabled, Attribution = provider.Attribution,
        ExternalCoordinateDisclosure = provider.ExternalCoordinateDisclosure ?? string.Empty,
        VerificationFromLongitude = provider.VerificationFromLongitude, VerificationFromLatitude = provider.VerificationFromLatitude,
        VerificationToLongitude = provider.VerificationToLongitude, VerificationToLatitude = provider.VerificationToLatitude,
        GenerationTimeoutSeconds = provider.GenerationTimeoutSeconds, ResponseSizeLimitBytes = provider.ResponseSizeLimitBytes,
        RequestsPerMinute = provider.RequestsPerMinute, MaxConcurrency = provider.MaxConcurrency,
        RowVersion = provider.RowVersion, ConfigurationVersion = provider.ConfigurationVersion,
        Mappings = provider.ProfileMappings.Select(item => new RoutingProviderMappingViewModel
            { TransportProfileId = item.TransportProfileId, OsrmProfile = item.OsrmProfile }).ToList()
    };

    private string AdministratorId() => User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "admin";
    private void Success(string message) => SetResult(true, message);
    private void SetResult(bool succeeded, string message)
    {
        TempData["AlertType"] = succeeded ? "success" : "danger";
        TempData["AlertMessage"] = message;
    }
}
