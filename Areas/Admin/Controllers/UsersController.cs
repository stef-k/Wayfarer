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
                UserName = user.UserName,
                DisplayName = user.DisplayName
            };
            LogAction("Admin GET", $"Change password for user {user.UserName}");
            SetPageTitle("Change User Password");
            return View(model);  // Return the ChangePassword view with the model containing the user ID
        }

        /// <summary>
        /// Change user password
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
                if (currentUser.UserName == user.UserName)
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

                // Remove the current password
                IdentityResult removePasswordResult = await _userManager.RemovePasswordAsync(user);
                if (!removePasswordResult.Succeeded)
                {
                    ModelState.AddModelError(string.Empty, "Error removing old password.");
                    LogAudit("Password Change Failed", "Error removing old password", "Failed to remove old password.");
                    LogAction("Password Change Failed", "Error removing old password");
                    return View(model);
                }

                // Add the new password
                IdentityResult addPasswordResult = await _userManager.AddPasswordAsync(user, model.NewPassword);
                if (addPasswordResult.Succeeded)
                {
                    // Update security stamp to invalidate sessions
                    await _userManager.UpdateSecurityStampAsync(user);

                    LogAudit("Password Changed", "Password changed successfully", $"Password changed for user {user.UserName}");
                    LogAction("Password Changed", $"Password changed for user {user.UserName}");

                    return RedirectWithAlert("Index", "Users", $"Password for user: {user.UserName} changed successfully. They have been logged out.", "success", routeValues: null, area: "Admin");
                }
                else
                {
                    foreach (IdentityError error in addPasswordResult.Errors)
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
            List<string?> roles = _roleManager.Roles.Select(r => r.Name).ToList();

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
            List<string?> roles = _roleManager.Roles.Select(r => r.Name).ToList();
            model.Roles = new SelectList(roles, model.Role);  // Re-populate roles dropdown with the selected role

            // Log all ModelState errors before any custom validation (for debugging)
            foreach (string key in ModelState.Keys)
            {
                Microsoft.AspNetCore.Mvc.ModelBinding.ModelStateEntry? state = ModelState[key];
                foreach (Microsoft.AspNetCore.Mvc.ModelBinding.ModelError error in state.Errors)
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
                Username = user.UserName,
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

            if (user != null && user.IsProtected)
            {
                RedirectWithAlert("Delete", "Users", "This user cannot be deleted as it is protected.", "danger", new { id }, "Admin");
            }

            IdentityResult result = await _userManager.DeleteAsync(user);
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

            string? userRole = (await _userManager.GetRolesAsync(user)).FirstOrDefault(); // Get the first role if there's one

            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            EditUserViewModel model = new EditUserViewModel
            {
                Id = user.Id,
                UserName = user.UserName,
                DisplayName = user.DisplayName,
                IsProtected = user.IsProtected,
                IsActive = user.IsActive, // Or whatever property you have
                Role = userRole, // Assign the role to the model
                IsCurrentUser = user.UserName == currentUser.UserName
            };

            LogAction("Admin GET", $"Edit user {user.UserName}");

            SetPageTitle("Edit User");
            return View(model);
        }

        /// <summary>
        /// Edit user
        /// </summary>
        /// <param name="model">EditUserViewModel</param>
        /// <returns></returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(EditUserViewModel model)
        {
            if (ModelState.IsValid)
            {
                ApplicationUser? user = await _userManager.FindByIdAsync(model.Id);
                if (user == null)
                {
                    return NotFound();
                }

                // Prevent changes to the default admin account
                if (user.UserName == "admin")
                {
                    ModelState.AddModelError("Role", "The default Administrator role cannot be changed.");
                    return View(model);
                }

                // Check if the user is the default admin and ensure they cannot be deactivated
                if (user.UserName == "admin" && model.IsActive == false)
                {
                    ModelState.AddModelError("IsActive", "The default Administrator user cannot be deactivated.");
                    return View(model);
                }

                // Prevent the default admin from changing their username
                if (user.UserName == "admin" && model.UserName != user.UserName)
                {
                    ModelState.AddModelError("UserName", "The default Administrator user cannot change their username.");
                    return View(model);
                }

                // Prevent any user from changing the username
                if (user.UserName != model.UserName)
                {
                    ModelState.AddModelError("UserName", "Username changes are not allowed.");
                    return View(model);
                }

                // Update user fields such as IsActive and IsProtected
                user.DisplayName = model.DisplayName;
                user.IsActive = model.IsActive;
                user.IsProtected = model.IsProtected;

                // Remove the current roles
                IList<string> currentRoles = await _userManager.GetRolesAsync(user);
                IdentityResult roleResult = await _userManager.RemoveFromRolesAsync(user, currentRoles);
                if (!roleResult.Succeeded)
                {
                    ModelState.AddModelError(string.Empty, "Failed to remove existing roles.");
                    return View(model);
                }

                // If not the default admin, allow updating the role
                if (user.UserName != "admin" && !string.IsNullOrEmpty(model.Role))
                {
                    roleResult = await _userManager.AddToRoleAsync(user, model.Role);
                }

                if (roleResult.Succeeded)
                {
                    // Update user in the database after roles are changed
                    IdentityResult updateResult = await _userManager.UpdateAsync(user);
                    if (updateResult.Succeeded)
                    {
                        // Log the role change to the database
                        LogAudit("Edit User", "User Data Changed", $"User {model.UserName} role: {model.Role}, IsActive: {model.IsActive}");
                        LogAction("User Edit", $"User {model.UserName} role: {model.Role}, IsActive: {model.IsActive}");

                        // Invalidate user sessions by updating the security stamp
                        await _userManager.UpdateSecurityStampAsync(user);
                        LogAction("User Edit", $"Invalidate User {user.UserName} sessions after role change");

                        // Redirect to index after saving
                        return RedirectToAction("Index");
                    }
                    else
                    {
                        foreach (IdentityError error in updateResult.Errors)
                        {
                            ModelState.AddModelError(string.Empty, error.Description);
                        }
                    }
                }
                else
                {
                    foreach (IdentityError error in roleResult.Errors)
                    {
                        ModelState.AddModelError(string.Empty, error.Description);
                    }
                }
            }

            return View(model);  // If validation fails, re-display the form
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
                usersQuery = usersQuery.Where(u =>
                    EF.Functions.ILike(u.UserName, $"%{search}%") ||
                    EF.Functions.ILike(u.DisplayName, $"%{search}%"));
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
                    Username = user.UserName,
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