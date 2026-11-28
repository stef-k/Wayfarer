using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Wayfarer.Localization;
using Wayfarer.Models;
using Wayfarer.Services.LocationImports;

namespace Wayfarer.Areas.Identity.Pages.Account.Manage;

/// <summary>Confirms self-service erasure and orders it with managed imports and provider admission.</summary>
[Authorize]
public sealed class DeletePersonalDataModel(ApplicationDbContext db, UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn, WebMessages messages) : PageModel
{
    /// <summary>Shows confirmation input only for an account with a password.</summary>
    public bool RequirePassword { get; private set; }

    /// <summary>Captures the password confirmation without retaining it across requests.</summary>
    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Loads the current account's confirmation form.</summary>
    public async Task<IActionResult> OnGetAsync()
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return MissingAccount();
        RequirePassword = await users.HasPasswordAsync(user);
        return Page();
    }

    /// <summary>Confirms the current account, deletes it transactionally and then clears the session.</summary>
    public async Task<IActionResult> OnPostAsync(CancellationToken token)
    {
        var user = await users.GetUserAsync(User);
        if (user is null) return MissingAccount();

        RequirePassword = await users.HasPasswordAsync(user);
        if (RequirePassword)
        {
            if (!ModelState.IsValid) return Page();
            if (!await users.CheckPasswordAsync(user, Input.Password ?? string.Empty))
            {
                ModelState.AddModelError("Input.Password", messages["Identity.Password.Incorrect"]);
                return Page();
            }
        }

        var deleted = await ManagedImportAccountDeletion.DeleteAsync(db, users, user, token);
        if (!deleted.Succeeded)
        {
            foreach (var error in deleted.Errors) ModelState.AddModelError(string.Empty, error.Description);
            return Page();
        }

        await signIn.SignOutAsync();
        return Redirect("~/");
    }

    /// <summary>Uses the Identity presentation filter's localized missing-account response.</summary>
    private NotFoundObjectResult MissingAccount() => NotFound($"Unable to load user with ID '{users.GetUserId(User)}'.");

    /// <summary>Self-service password confirmation.</summary>
    public sealed class InputModel
    {
        /// <summary>The signed-in account's password, required when displayed.</summary>
        [Required]
        [DataType(DataType.Password)]
        public string? Password { get; set; }
    }
}
