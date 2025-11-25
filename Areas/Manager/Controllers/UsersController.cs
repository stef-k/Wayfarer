using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Util;

namespace Wayfarer.Areas.Manager.Controllers
{
    [Area("Manager")]
    [Authorize(Roles = "Manager")]
    public class UsersController : BaseController
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly RoleManager<IdentityRole> _roleManager;
        private readonly ApiTokenService _apiTokenService;

        public UsersController(UserManager<ApplicationUser> userManager,
                                RoleManager<IdentityRole> roleManager,
                                ILogger<BaseController> logger,
                                ApplicationDbContext dbContext,
                                ApiTokenService apiTokenService) : base(logger, dbContext)
        {
            _userManager = userManager;
            _roleManager = roleManager;
            _apiTokenService = apiTokenService;
        }

        /// <summary>
        /// Get all users with role User.
        /// </summary>
        /// <param name="search"></param>
        /// <param name="page"></param>
        /// <returns></returns>
        public async Task<IActionResult> Index(string search, int page = 1)
        {
            _logger.LogInformation("Manager Users index page accessed");
            const int PageSize = 10; // Number of items per page
            string[] roleNames = new[] { "User" };

            IEnumerable<ApplicationUser> usersQuery = await GetUsersByRolesAsync(roleNames);

            if (!string.IsNullOrEmpty(search))
            {
                usersQuery = usersQuery.Where(u =>
                    EF.Functions.ILike(u.UserName ?? string.Empty, $"%{search}%") ||
                    EF.Functions.ILike(u.DisplayName ?? string.Empty, $"%{search}%"));
            }

            int totalUsers = usersQuery.Count();
            List<UserViewModel> users = usersQuery
                .Skip((page - 1) * PageSize)
                .Take(PageSize)
                .Select(u => new UserViewModel
                {
                    Id = u.Id,
                    Username = u.UserName ?? string.Empty,
                    DisplayName = u.DisplayName,
                    Role = string.Join(", ", _userManager.GetRolesAsync(u).Result),
                    IsActive = u.IsActive,
                    IsProtected = u.IsProtected, // Example condition
                    IsCurrentUser = User.Identity?.Name == u.UserName // Example condition
                }).ToList();

            ViewBag.TotalPages = (int)Math.Ceiling(totalUsers / (double)PageSize);
            ViewBag.CurrentPage = page;
            ViewBag.Search = search;

            SetPageTitle("User Management");
            return View(users);
        }

        /// <summary>
        /// Create a new user (for Manager role)
        /// GET: Admin/Create
        /// </summary>
        /// <returns></returns>
        [HttpGet]
        public IActionResult Create()
        {
            // Get only the role 'User' for Manager
            string[] allowedRoles = new[] { "User" };

            // Use GetUsersByRolesAsync from the base controller to get the users with these roles
            IEnumerable<ApplicationUser> users = GetUsersByRolesAsync(allowedRoles).Result;

            // Populate the roles dropdown with available roles for Manager
            List<string?> roles = _roleManager.Roles.Where(r => allowedRoles.Contains(r.Name)).Select(r => r.Name).ToList();

            CreateUserViewModel model = new CreateUserViewModel
            {
                Roles = new SelectList(roles)
            };
            SetPageTitle("Create New User");
            return View(model);
        }

        /// <summary>
        /// Create a new user (for Manager role)
        /// </summary>
        /// <param name="model">CreateUserViewModel</param>
        /// <returns></returns>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create(CreateUserViewModel model)
        {
            // Get only the role 'User' for Manager
            string[] allowedRoles = new[] { "User" };

            // Use GetUsersByRolesAsync from the base controller to get the users with these roles
            IEnumerable<ApplicationUser> users = GetUsersByRolesAsync(allowedRoles).Result;

            // Populate the roles dropdown with available roles for Manager
            List<string?> roles = _roleManager.Roles.Where(r => allowedRoles.Contains(r.Name)).Select(r => r.Name).ToList();

            model.Roles = new SelectList(roles, model.Role);

            // Validate role selection
            if (string.IsNullOrEmpty(model.Role))
            {
                ModelState.AddModelError("Role", "Please select a role.");
                return View(model);
            }

            if (!allowedRoles.Contains(model.Role))
            {
                ModelState.AddModelError("Role", "Managers can only create users with the 'User' role.");
                return View(model);
            }

            // Check if the username already exists
            ApplicationUser? existingUser = await _userManager.FindByNameAsync(model.UserName);
            if (existingUser != null)
            {
                ModelState.AddModelError("UserName", "The username is already taken.");
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

            IdentityResult result = await _userManager.CreateAsync(user, model.Password);
            if (result.Succeeded)
            {
                // Assign the selected role to the user
                await _userManager.AddToRoleAsync(user, model.Role);

                string tokenName = "Wayfarer Incoming Location Data API Token";
                await _apiTokenService.CreateApiTokenAsync(user.Id, tokenName);

                // Log successful creation
                LogAudit("User Created", $"User {user.UserName} created successfully with role {model.Role}", $"User {user.UserName} created and assigned role {model.Role}");
                LogAction("User Created", $"User {user.UserName} created and assigned role {model.Role}");

                // Redirect to the index page with success message
                return RedirectWithAlert("Index", "Users", "User created successfully", "success", routeValues: null, "Manager");
            }

            // Handle errors if user creation fails
            foreach (IdentityError error in result.Errors)
            {
                ModelState.AddModelError("", error.Description);
            }

            LogAction("Manager POST", "User creation failed due to validation errors");

            return View(model);
        }

        /// <summary>
        /// Delete a user (GET)
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        [HttpGet]
        public async Task<IActionResult> Delete(string id)
        {
            ApplicationUser? user = await _userManager.FindByIdAsync(id);
            if (user == null)
            {
                return NotFound();
            }

            // Ensure the manager can only delete User role accounts
            IList<string> roles = await _userManager.GetRolesAsync(user);
            if (!roles.Contains("User"))
            {
                return RedirectToAction("AccessDenied", "Account");
            }

            DeleteUserViewModel model = new DeleteUserViewModel
            {
                Id = user.Id,
                Username = user.UserName ?? string.Empty,
                DisplayName = user.DisplayName,
            };

            SetPageTitle("Delete User");
            return View(model);
        }

        /// <summary>
        /// Delete the user (POST)
        /// </summary>
        /// <param name="id"></param>
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

            // Check if the user is protected, and if so, prevent deletion
            if (user != null && user.IsProtected)
            {
                return RedirectWithAlert("Delete", "Users", "This user cannot be deleted as it is protected.", "danger", new { id }, "Manager");
            }

            IdentityResult result = await _userManager.DeleteAsync(user);
            if (result.Succeeded)
            {
                LogAudit("User Data Changed", "User has been deleted", $"User with username: {user.UserName} has been deleted.");
                LogAction("User Delete", $"User with username: {user.UserName} has been deleted.");

                // Invalidate the session for the deleted user
                await _userManager.UpdateSecurityStampAsync(user);
                LogAction("Security Stamp Update", $"Security stamp for user {user.UserName} updated to invalidate sessions.");

                // Redirect after deletion
                return RedirectWithAlert("Index", "Users", "User has been deleted", "success", routeValues: null, "Manager");
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
        /// GET: Users/Edit/{id}
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

            // Get the user's role
            string? userRole = (await _userManager.GetRolesAsync(user)).FirstOrDefault();

            // Get the current user (the manager performing the edit)
            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            // Check if the current user is a Manager and prevent them from editing Admin/Manager roles
            if (userRole != "User")
            {
                return RedirectToAction("Index", "Users");
            }

            // Set the model
            EditUserViewModel model = new EditUserViewModel
            {
                Id = user.Id,
                UserName = user.UserName ?? string.Empty,
                DisplayName = user.DisplayName,
                IsProtected = user.IsProtected,
                IsActive = user.IsActive,
                Role = userRole ?? string.Empty, // Assign the role to the model
                IsCurrentUser = user.UserName == currentUser?.UserName
            };
            LogAudit("Manager User Edit", "User Edit GET", $"Manager is editing user {user.UserName}");
            LogAction("Manager-User Edit GET", $"Manager is editing user {user.UserName}");

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

                // Check if the manager is trying to change the role to Admin or Manager
                if (user.UserName != "admin" && (model.Role == "Admin" || model.Role == "Manager"))
                {
                    ModelState.AddModelError("Role", "Managers are not allowed to change roles to Admin or Manager.");
                    return View(model);
                }

                // Prevent any user from changing their username
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

                // Only allow updating roles to User
                if (model.Role == "User")
                {
                    roleResult = await _userManager.AddToRoleAsync(user, model.Role);
                }
                else
                {
                    ModelState.AddModelError("Role", "Managers can only assign the 'User' role.");
                    return View(model);
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

                        return RedirectWithAlert("Index", "Users", $"User {model.UserName} data has been updated successfuly", "success", routeValues: null, "Manager");
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
        /// Change user password
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

            // Only allow managers to change passwords for User role accounts
            if (!await _userManager.IsInRoleAsync(user, "User"))
            {
                return Forbid(); // Return Forbidden if the user is not a User role
            }

            ChangePasswordViewModel model = new ChangePasswordViewModel
            {
                UserId = id,
                UserName = user.UserName,
                DisplayName = user.DisplayName
            };
            LogAction("Manager GET", $"Change password for user {user.UserName}");
            SetPageTitle("Change User Password");
            return View(model);  // Return the ChangePassword view with the model containing the user ID
        }

        /// <summary>
        /// Change user password
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

                // Only allow managers to change passwords for User role accounts
                if (!await _userManager.IsInRoleAsync(user, "User"))
                {
                    return Forbid(); // Return Forbidden if the user is not a User role
                }

                if (model.NewPassword != model.ConfirmPassword)
                {
                    ModelState.AddModelError(string.Empty, "The new password and confirmation password do not match.");
                    LogAction("Manager POST", "Password change failed due to mismatched passwords");
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

                    return RedirectWithAlert("Index", "Users", $"User {user.UserName} password has been updated successfuly", "success", routeValues: null, "Manager");
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
    }
}
