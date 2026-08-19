using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Areas.User.RoutingModels;
using Wayfarer.Models;
using Wayfarer.Services.ExternalRouting;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>Manages only the authenticated user's approved personal routing selection.</summary>
[Area("User"), Authorize(Roles = "User")]
public sealed class RoutingSettingsController(
    ApplicationDbContext dbContext, UserRoutingConfigurationService configurations,
    PersonalRoutingVerificationService verification, UserRoutingCredentialService credentials) : Controller
{
    /// <summary>Displays masked current-user routing settings.</summary>
    public async Task<IActionResult> Index(CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        var model = await BuildAsync(userId, null, cancellationToken);
        return model == null ? NotFound() : View(model);
    }

    /// <summary>Saves server-default or an eligible personal selection.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(RoutingSettingsViewModel model, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        if (ModelState.IsValid)
        {
            var result = await configurations.SaveAsync(
                userId, model.SelectedProviderConfigurationId, model.Credential, model.RowVersion, cancellationToken);
            if (result.Missing) return NotFound();
            if (result.Succeeded) return RedirectToAction(nameof(Index));
            ModelState.AddModelError(string.Empty, result.Error!);
        }
        model.Credential = null;
        return View("Index", await BuildAsync(userId, model, cancellationToken));
    }

    /// <summary>Clears a personal credential through a separate confirmed action.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> ClearCredential(bool confirmed, uint rowVersion, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        var result = await configurations.ClearAsync(userId, confirmed, rowVersion, cancellationToken);
        if (result.Missing) return NotFound();
        if (!result.Succeeded) TempData["AlertMessage"] = result.Error;
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Verifies the current user's required personal credential.</summary>
    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> Verify(uint rowVersion, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (userId == null) return Challenge();
        var result = await verification.VerifyAsync(userId, rowVersion, cancellationToken);
        TempData["AlertType"] = result.Succeeded ? "success" : "danger";
        TempData["AlertMessage"] = result.Succeeded
            ? "Personal routing credential verified." : "Personal routing verification is unavailable or stale.";
        return RedirectToAction(nameof(Index));
    }

    private async Task<RoutingSettingsViewModel?> BuildAsync(
        string userId, RoutingSettingsViewModel? submitted, CancellationToken cancellationToken)
    {
        var configuration = await dbContext.Set<UserRoutingConfiguration>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
        if (configuration == null) return null;
        var providers = await dbContext.Set<RoutingProviderConfiguration>().AsNoTracking()
            .Include(item => item.ProfileMappings).ThenInclude(item => item.TransportProfile)
            .OrderBy(item => item.DisplayName).ToArrayAsync(cancellationToken);
        var templates = providers.Where(item => PersonalRoutingEligibility.Evaluate(item).Eligible)
            .Select(item => new RoutingTemplateViewModel(item.Id, item.DisplayName,
                item.PersonalRoutingAccess == PersonalRoutingAccess.CredentialRequired,
                item.ExternalCoordinateDisclosure!)).ToArray();
        var status = ResolveStatus(configuration, providers);
        return new RoutingSettingsViewModel
        {
            SelectedProviderConfigurationId = submitted?.SelectedProviderConfigurationId ?? configuration.SelectedProviderConfigurationId,
            CredentialPresent = configuration.CredentialPresent, RowVersion = configuration.RowVersion,
            Status = status, Templates = templates
        };
    }

    private string ResolveStatus(
        UserRoutingConfiguration configuration, IReadOnlyList<RoutingProviderConfiguration> providers)
    {
        if (configuration.SelectedProviderConfigurationId == null) return "Ready";
        var provider = providers.SingleOrDefault(item => item.Id == configuration.SelectedProviderConfigurationId);
        if (provider == null || !PersonalRoutingEligibility.Evaluate(provider).Eligible) return "Unavailable";
        if (provider.PersonalRoutingAccess == PersonalRoutingAccess.CredentialFree)
            return configuration.CredentialPresent ? "Unavailable" : "Ready";
        if (!configuration.CredentialPresent
            || !credentials.Unprotect(configuration.UserId, provider.Id, configuration.CredentialCiphertext).Succeeded)
            return "Unavailable";
        return configuration.VerificationStatus == "verified"
            && configuration.VerifiedUserConfigurationVersion == configuration.ConfigurationVersion
            && configuration.VerifiedProviderConfigurationVersion == provider.ConfigurationVersion
            ? "Verified" : "Ready";
    }
}
