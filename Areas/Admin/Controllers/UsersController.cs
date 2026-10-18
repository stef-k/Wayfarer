using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Util;

namespace Wayfarer.Areas.Admin.Controllers
{
    [Area("Admin")]
    [Authorize(Roles = "Admin")] // This attribute restricts access to the UsersController to users in the Admin role.
    public class UsersController : BaseController
    {
        // Admin edits always replace membership with one supported installation role.
        private static readonly string[] AllowedEditRoles =
            [ApplicationRoles.Admin, ApplicationRoles.Manager, ApplicationRoles.User];
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly SignInManager<ApplicationUser> _signInManager;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly ApiTokenService _apiTokenService;

        public UsersController(ApplicationDbContext dbContext,
            UserManager<ApplicationUser> userManager,
            RoleManager<IdentityRole> roleManager,
            SignInManager<ApplicationUser> signInManager,
            ILogger<UsersController> logger,
            ApiTokenService apiTokenService) : base(logger, dbContext)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _signInManager = signInManager;
            _apiTokenService = apiTokenService;
        }

        /// <summary>
        /// Change user password
        /// GET: Admin/ChangePassword/{id}
        /// </summary>
        /// <param name="id">The ID of the user to change the password for</param>
        /// <returns></returns>
        public async Task<IActionResult> ChangePassword(string id)
        {
            if (string.IsNullOrEmpty(id))
            {
                return NotFound();
            }

            ApplicationUser? user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                return NotFound();
            }

            ChangePasswordViewModel model = new ChangePasswordViewModel
            {
                UserId = id,
                UserName = user.UserName ?? string.Empty,
                DisplayName = user.DisplayName
            };
            LogAction("Admin GET", $"Change password for user {user.UserName}");
            SetPageTitle("Change User Password");
            return View(model);  // Return the ChangePassword view with the model containing the user ID
        }

        /// <summary>
        /// Validate and reset a user password through Identity before replacing the existing credential.
        /// POST: Admin/ChangePassword/{userId}
        /// </summary>
        /// <param name="model">ChangePasswordViewModel</param>
        /// <returns></returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ChangePassword(ChangePasswordViewModel model)
        {
            if (ModelState.IsValid)
            {
                ApplicationUser? user = await _userManager.FindByIdAsync(model.UserId);
                if (user == null)
                {
                    return NotFound();
                }

                ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

                // Prevent the current user (admin) from changing their own password on this page
                if (currentUser?.UserName == user.UserName)
                {
                    ModelState.AddModelError(string.Empty, "You cannot change your own password from this page. Please go to the identity pages to update your password.");
                    LogAction("Admin POST", "Admin attempted to change their own password from the admin page.");
                    return View(model);
                }

                if (model.NewPassword != model.ConfirmPassword)
                {
                    ModelState.AddModelError(string.Empty, "The new password and confirmation password do not match.");
                    LogAction("Admin POST", "Password change failed due to mismatched passwords");
                    return View(model);
                }

                // Identity validates before mutation and updates the security stamp only on success.
                string resetToken = await _userManager.GeneratePasswordResetTokenAsync(user);
                IdentityResult resetPasswordResult = await _userManager.ResetPasswordAsync(user, resetToken, model.NewPassword);
                if (resetPasswordResult.Succeeded)
                {
                    LogAudit("Password Changed", "Password changed successfully", $"Password changed for user {user.UserName}");
                    LogAction("Password Changed", $"Password changed for user {user.UserName}");

                    return RedirectWithAlert("Index", "Users", $"Password for user: {user.UserName} changed successfully. They have been logged out.", "success", routeValues: null, area: "Admin");
                }
                else
                {
                    foreach (IdentityError error in resetPasswordResult.Errors)
                    {
                        ModelState.AddModelError(string.Empty, error.Description);
                    }
                }
            }

            return View(model); // If validation fails, re-display the form
        }

        /// <summary>
        /// Create a new user
        /// GET: Admin/Create
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public IActionResult Create()
        {
            // Populate the roles select list with available roles
            string[] allowedRoles =
            {
                ApplicationRoles.Admin,
                ApplicationRoles.Manager,
                ApplicationRoles.User
            };
            List<string> roles = _roleManager.Roles
                .Select(r => r.Name)
                .Where(name => name != null && allowedRoles.Contains(name))
                .Select(name => name!)
                .ToList();

            // Create the view model and pass the list of roles
            CreateUserViewModel model = new CreateUserViewModel
            {
                Roles = new SelectList(roles)
            };
            SetPageTitle("Create New User");
            return View(model);
        }

        /// <summary>
        /// Create a new user
        /// </summary>
        /// <param name="model">CreateUserViewModel</param>
        /// <returns></returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreateUserViewModel model)
        {
            // Ensure roles are populated on each request (including POST)
            string[] allowedRoles =
            {
                ApplicationRoles.Admin,
                ApplicationRoles.Manager,
                ApplicationRoles.User
            };
            List<string> roles = _roleManager.Roles
                .Select(r => r.Name)
                .Where(name => name != null && allowedRoles.Contains(name))
                .Select(name => name!)
                .ToList();
            model.Roles = new SelectList(roles, model.Role);  // Re-populate roles dropdown with the selected role

            // Log all ModelState errors before any custom validation (for debugging)
            foreach (string key in ModelState.Keys)
            {
                Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateEntry? state = ModelState[key];
                foreach (Microsoft.AspNetCore.Mvc.ModelBinding.ModelError error in state?.Errors ?? Enumerable.Empty<Microsoft.AspNetCore.Mvc.ModelBinding.ModelError>())
                {
                    LogAction("Validation Error", $"Key: {key}, Error: {error.ErrorMessage}");
                }
            }

            // Validate Role field specifically
            if (string.IsNullOrEmpty(model.Role))
            {
                ModelState.AddModelError("Role", "Please select a role.");
                LogAction("Admin POST", "User creation failed due to no role selected");
                return View(model);
            }

            if (!allowedRoles.Contains(model.Role))
            {
                ModelState.AddModelError("Role", "Selected role is not valid.");
                LogAction("Admin POST", $"User creation failed due to invalid role selection: {model.Role}");
                return View(model);
            }

            // Check if the username already exists
            ApplicationUser? existingUser = await _userManager.FindByNameAsync(model.UserName);
            if (existingUser != null)
            {
                ModelState.AddModelError("UserName", "The username is already taken.");
                LogAction("Admin POST", "User creation failed due to existing username");
                return View(model);
            }

            // Create the user
            ApplicationUser user = new ApplicationUser
            {
                UserName = model.UserName,
                DisplayName = model.DisplayName,
                IsActive = model.IsActive,
                IsProtected = model.IsProtected
            };

            // Attempt to create the user in the system
            IdentityResult result = await _userManager.CreateAsync(user, model.Password);
            if (result.Succeeded)
            {
                // Assign the selected role to the user
                await _userManager.AddToRoleAsync(user, model.Role);

                string tokenName = "Wayfarer Incoming Location Data API Token";
                await _apiTokenService.CreateApiTokenAsync(user.Id, tokenName);

                // Log successful user creation and role assignment
                LogAudit("User Created", $"User {user.UserName} created successfully with role {model.Role}", $"User {user.UserName} created and assigned role {model.Role}");
                LogAction("User Created", $"User {user.UserName} created and assigned role {model.Role}");

                // Redirect to the index page with success message
                return RedirectWithAlert("Index", "Users", "User created successfully", "success", routeValues: null, "Admin");
            }

            // Log errors if user creation fails and populate ModelState with the errors
            foreach (IdentityError error in result.Errors)
            {
                ModelState.AddModelError("", error.Description);
            }

            // Log action failure with validation error message
            LogAction("Admin POST", "User creation failed due to validation errors");

            // Return the view with validation errors if ModelState is invalid
            return View(model);
        }

        // Display confirmation page before deleting the user (GET)
        [HttpGet]
        public async Task<IActionResult> Delete(string id)
        {
            ApplicationUser? user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                return NotFound();
            }

            DeleteUserViewModel model = new DeleteUserViewModel
            {
                Id = user.Id,
                Username = user.UserName ?? string.Empty,
                DisplayName = user.DisplayName,
            };
            LogAction("Admin GET", $"Delete user {user.UserName}");

            SetPageTitle("Delete User");
            return View(model);
        }

        // UsersController
        /// <summary>
        /// Sign Out and Delete user (POST)
        /// </summary>
        /// <param name="id">User ID</param>
        /// <returns></returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            ApplicationUser? user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                return NotFound();
            }

            if (user.IsProtected)
            {
                // Note: In Admin area, we allow deletion of protected users but show a warning
                // This is intentional - admins have override capability
                RedirectWithAlert("Delete", "Users", "Warning: Deleting a protected user.", "warning", new { id }, "Admin");
            }

            IdentityResult result = await _userManager.DeleteAsync(user!);
            if (result.Succeeded)
            {
                LogAudit("User Data Changed", "User has been deleted", $"User with : {user.UserName} has been deleted.");
                LogAction("User Delete", $"User with : {user.UserName} has been deleted.");

                // Invalidate all sessions for the user
                await _userManager.UpdateSecurityStampAsync(user);
                LogAction("Security Stamp Update", $"Security stamp for user {user.UserName} updated to invalidate sessions.");

                // If the user being deleted is the admin, we sign them back in after deletion
                ApplicationUser? adminUser = await _userManager.GetUserAsync(User); // Get the current logged-in user (admin)
                if (adminUser != null)
                {
                    await _signInManager.SignInAsync(adminUser, isPersistent: false);
                    LogAction("Admin Sign In", $"Admin {adminUser.UserName} signed in again after user edit.");
                }

                // Redirect after deletion
                return RedirectWithAlert("Index", "Users", "User has been deleted", "success", routeValues: null, "Admin");
            }

            // Handle failure
            foreach (IdentityError error in result.Errors)
            {
                ModelState.AddModelError(string.Empty, error.Description);
            }
            return View();
        }

        /// <summary>
        /// Edit user
        /// GET: Admin/Edit/{id}
        /// </summary>
        /// <param name="id">EditUserViewModel</param>
        /// <returns></returns>
        public async Task<IActionResult> Edit(string id)
        {
            if (id == null)
            {
                return NotFound();
            }

            ApplicationUser? user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                return NotFound();
            }

            IList<string> roles = await _userManager.GetRolesAsync(user);
            // Ambiguous or unsupported memberships require a deliberate replacement selection.
            string? userRole = roles.Count == 1 && AllowedEditRoles.Contains(roles[0]) ? roles[0] : null;

            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            EditUserViewModel model = new EditUserViewModel
            {
                Id = user.Id,
                UserName = user.UserName ?? string.Empty,
                DisplayName = user.DisplayName,
                IsProtected = user.IsProtected,
                IsActive = user.IsActive, // Or whatever property you have
                Role = userRole ?? string.Empty, // Assign the role to the model
                IsCurrentUser = user.UserName == currentUser?.UserName
            };

            LogAction("Admin GET", $"Edit user {user.UserName}");

            SetPageTitle("Edit User");
            return View(model);
        }

        /// <summary>Validates an explicit role, then atomically saves account fields, membership, stamp and audit.</summary>
        /// <param name="model">Submitted Admin edit fields; Manager validation remains independent.</param>
        /// <returns>The existing success redirect or the form with validation errors.</returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(EditUserViewModel model)
        {
            if (string.IsNullOrWhiteSpace(model.Role))
                ModelState.AddModelError(nameof(model.Role), "Please select a role.");
            if (!ModelState.IsValid) return View(model);

            ApplicationUser? user = await _userManager.FindByIdAsync(model.Id);
            if (user == null) return NotFound();
            // The default account is immutable here, including its username, activity and protection.
            if (user.UserName == "admin")
            {
                ModelState.AddModelError(nameof(model.Role), "The default Administrator role cannot be changed.");
                return View(model);
            }
            if (user.UserName != model.UserName)
            {
                ModelState.AddModelError(nameof(model.UserName), "Username changes are not allowed.");
                return View(model);
            }

            string? role = AllowedEditRoles.FirstOrDefault(candidate =>
                _userManager.NormalizeName(candidate) == _userManager.NormalizeName(model.Role));
            if (role == null || !await _roleManager.RoleExistsAsync(role))
            {
                ModelState.AddModelError(nameof(model.Role), "Selected role is not valid.");
                return View(model);
            }

            CancellationToken cancellation = HttpContext.RequestAborted;
            cancellation.ThrowIfCancellationRequested();
            await using (var transaction = await _dbContext.Database.BeginTransactionAsync(cancellation))
            {
                IList<string> currentRoles = await _userManager.GetRolesAsync(user);
                cancellation.ThrowIfCancellationRequested();
                user.DisplayName = model.DisplayName;
                user.IsActive = model.IsActive;
                user.IsProtected = model.IsProtected;

                // Identity updates the parent account row, retaining #823's lock conflict through commit.
                // Only an already exact single membership can safely avoid role-table rewrites.
                if (currentRoles.Count != 1 || _userManager.NormalizeName(currentRoles[0]) != _userManager.NormalizeName(role))
                {
                    if (!AcceptEditResult(await _userManager.RemoveFromRolesAsync(user, currentRoles))) return View(model);
                    if (!AcceptEditResult(await _userManager.AddToRoleAsync(user, role))) return View(model);
                }
                if (!AcceptEditResult(await _userManager.UpdateAsync(user))) return View(model);
                if (!AcceptEditResult(await _userManager.UpdateSecurityStampAsync(user))) return View(model);

                // LogAudit synchronously saves this same context and joins the enclosing transaction.
                LogAudit("Edit User", "User Data Changed", $"User {model.UserName} role: {role}, IsActive: {model.IsActive}");
                cancellation.ThrowIfCancellationRequested();
                await transaction.CommitAsync(cancellation);
            }

            // Failure paths never call a saving error helper or publish an external success message.
            LogAction("User Edit", $"User {model.UserName} role: {role}, IsActive: {model.IsActive}");
            LogAction("User Edit", $"Invalidate User {user.UserName} sessions after role change");
            return RedirectToAction("Index");
        }

        /// <summary>Surfaces checked Identity errors without saving any failed tracked changes.</summary>
        private bool AcceptEditResult(IdentityResult result)
        {
            foreach (IdentityError error in result.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            return result.Succeeded;
        }

        /// <summary>
        /// Admin: Get all users with pagination and search.
        /// </summary>
        /// <param name="search">Search query string.</param>
        /// <param name="page">Current page number.</param>
        /// <returns>View with paginated and searchable user list.</returns>
        public async Task<IActionResult> Index(string search, int page = 1)
        {
            const int PageSize = 10; // Number of items per page
            _logger.LogInformation("Admin Users index page accessed");

            // Get all users from the database
            IQueryable<ApplicationUser> usersQuery = _userManager.Users.AsQueryable();

            if (!string.IsNullOrEmpty(search))
            {
                string searchPattern = $"%{search}%";
                usersQuery = usersQuery.Where(u =>
                    EF.Functions.ILike(u.UserName != null ? u.UserName : string.Empty, searchPattern) ||
                    EF.Functions.ILike(u.DisplayName != null ? u.DisplayName : string.Empty, searchPattern));
            }

            int totalUsers = await usersQuery.CountAsync();

            List<ApplicationUser> users = await usersQuery
                .OrderBy(u => u.UserName) // Order users alphabetically (or by a preferred field)
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .ToListAsync();

            List<UserViewModel> userList = new List<UserViewModel>();
            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            foreach (ApplicationUser? user in users)
            {
                IList<string> roles = await _userManager.GetRolesAsync(user);
                userList.Add(new UserViewModel
                {
                    Id = user.Id,
                    Username = user.UserName ?? string.Empty,
                    Role = string.Join(", ", roles) ?? "No Role",
                    DisplayName = user.DisplayName,
                    IsActive = user.IsActive,
                    IsProtected = user.IsProtected, // Example condition
                    IsCurrentUser = currentUser != null && user.UserName == currentUser.UserName
                });
            }

            // Set pagination-related ViewBag properties
            ViewBag.TotalPages = (int)Math.Ceiling(totalUsers / (double)PageSize);
            ViewBag.CurrentPage = page;
            ViewBag.Search = search;

            // Log admin action
            LogAction("Admin GET", "Viewed all users");

            SetPageTitle("User Management");
            return View(userList); // Pass the user list to the view
        }
    }
}
