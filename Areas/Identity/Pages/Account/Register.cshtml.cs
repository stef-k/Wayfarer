using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Util;

public class RegisterModel : PageModel
{
    private readonly UserManager<ApplicationUser> _userManager;
    private readonly RoleManager<IdentityRole> _roleManager;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly ILogger<RegisterModel> _logger;
    private readonly ApiTokenService _apiTokenService;
    private readonly IRegistrationService _registrationService;

    public RegisterModel(
        UserManager<ApplicationUser> userManager,
        RoleManager<IdentityRole> roleManager,
        SignInManager<ApplicationUser> signInManager,
        ILogger<RegisterModel> logger,
        ApiTokenService apiTokenService, 
        IRegistrationService registrationService)
    {
        _userManager = userManager;
        _roleManager = roleManager;
        _signInManager = signInManager;
        _logger = logger;
        _apiTokenService = apiTokenService;
        _registrationService = registrationService;
    }

    [BindProperty]
    public InputModel Input { get; set; }

    public string ReturnUrl { get; set; }

    public IList<AuthenticationScheme> ExternalLogins { get; set; }

    public class InputModel
    {
        [Required]
        [StringLength(100, ErrorMessage = "The {0} must be at least {2} characters long.", MinimumLength = 3)]
        [Display(Name = "UserName")]
        public string Username { get; set; }

        [Required]
        [DataType(DataType.Password)]
        [Display(Name = "Password")]
        public string Password { get; set; }

        [DataType(DataType.Password)]
        [Display(Name = "Confirm password")]
        [Compare("Password", ErrorMessage = "The password and confirmation password do not match.")]
        public string ConfirmPassword { get; set; }

        [Required]
        [StringLength(100)]
        public string DisplayName { get; set; }
    }

    public async Task OnGetAsync(string returnUrl = null)
    {
        // check if the app has registrations open
        _registrationService.CheckRegistration(HttpContext);
        ReturnUrl = returnUrl ?? Url.Content("~/");
        ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();
    }

    public async Task<IActionResult> OnPostAsync(string returnUrl = null)
    {
        // check if the app has registrations open
        _registrationService.CheckRegistration(HttpContext);
        returnUrl ??= Url.Content("~/");

        ExternalLogins = (await _signInManager.GetExternalAuthenticationSchemesAsync()).ToList();

        if (ModelState.IsValid)
        {
            // Check if username is already taken
            ApplicationUser? existingUser = await _userManager.FindByNameAsync(Input.Username);
            if (existingUser != null)
            {
                ModelState.AddModelError("Input.UserName", "UserName is already taken.");
                return Page();
            }

            //var user = new ApplicationUser { UserName = Input.UserName };
            ApplicationUser user = CreateUser(Input.DisplayName);

            user.UserName = Input.Username;
            user.IsActive = true;

            IdentityResult result = await _userManager.CreateAsync(user, Input.Password);

            if (result.Succeeded)
            {
                // Add user to the User role. This is the default role for all users using the registration page.
                await _userManager.AddToRoleAsync(user, ApplicationRoles.User);
                _logger.LogInformation("User created a new account with password.");

                string tokenName = "Wayfarer Incoming Location Data API Token";  // Adjust as needed
                await _apiTokenService.CreateApiTokenAsync(user.Id, tokenName);

                // Add any logic for login after registration or confirmation if needed

                return RedirectToPage("RegisterConfirmation", new { username = Input.Username, returnUrl });
            }

            foreach (IdentityError error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }
        }

        return Page();
    }

    private ApplicationUser CreateUser(string displayName)
    {
        try
        {
            ApplicationUser user = Activator.CreateInstance<ApplicationUser>();

            user.DisplayName = displayName;

            return user;
        }
        catch
        {
            throw new InvalidOperationException($"Can't create an instance of '{nameof(ApplicationUser)}'. " +
                $"Ensure that '{nameof(ApplicationUser)}' is not and abstract class and has a parameterless constructor, or alternatively " +
                $"override the register page in /Areas/Identity/Pages/Account/Register.cshtml");
        }
    }
}