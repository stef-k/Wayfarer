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
    ApplicationDbContext dbContext, UserRoutingConfigurationService configurations) : Controller
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
        return new RoutingSettingsViewModel
        {
            SelectedProviderConfigurationId = submitted?.SelectedProviderConfigurationId ?? configuration.SelectedProviderConfigurationId,
            CredentialPresent = configuration.CredentialPresent, RowVersion = configuration.RowVersion,
            Status = configuration.VerificationStatus == "verified" ? "Verified" : "Ready", Templates = templates
        };
    }
}
