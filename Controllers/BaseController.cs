using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;


using Wayfarer.Models;

/// <summary>
/// A base controller class providing common functionality for controllers, including error handling, 
/// validation, logging, and auditing actions (in both Serilog and PostgreSQL).
/// </summary>
public class BaseController : Controller
{
    protected readonly ILogger<BaseController> _logger;
    protected readonly ApplicationDbContext _dbContext;

    /// <summary>
    /// Constructor for BaseController with dependency injection.
    /// </summary>
    /// <param name="logger">Logger for logging actions.</param>
    /// <param name="dbContext">The database context.</param>
    public BaseController(ILogger<BaseController> logger, ApplicationDbContext dbContext)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _dbContext = dbContext ?? throw new ArgumentNullException(nameof(dbContext));

        // Set a default title if not set explicitly in the controller or view
        if (ViewData["Title"] == null)
        {
            ViewData["Title"] = "Wayfarer";  // Set a default title here
        }
    }

    /// <summary>
    /// Sets an alert message to be displayed on the next view, logging the action in both Serilog and PostgreSQL.
    /// </summary>
    /// <param name="message">The message to show</param>
    /// <param name="alertType">The type of alert to display (default is "success"). Can be "success", "danger", "warning", "info", etc.</param>
    protected void SetAlert(string message, string alertType = "success")
    {
        // Log success message to Serilog (console/file)
        _logger.LogInformation($"Alert: {message}");

        if (TempData != null)
        {
            TempData["AlertMessage"] = message;
            TempData["AlertType"] = alertType;
        }
    }

    /// <summary>
    /// Handles errors by logging them in Serilog and PostgreSQL audit logs,
    /// and sets a user-friendly alert message in TempData.
    /// </summary>
    /// <param name="ex">The exception that occurred.</param>
    public void HandleError(Exception ex)
    {
        // Log to Serilog (console/file)
        _logger.LogError(ex, "An error occurred during an action.");

        // Log to PostgreSQL for audit purposes
        LogAudit("Error", "An error occurred during an action.", ex.Message);

        // Optionally, set a user-friendly alert message
        SetAlert("An unexpected error occurred. Please try again later.", "danger");
    }

    /// <summary>
    /// Logs an audit entry to the database for the given action, description, and message.
    /// </summary>
    /// <param name="action">The action being performed (e.g., "Password Change", "User Update").</param>
    /// <param name="description">A short description of the action (e.g., "Password changed successfully").</param>
    /// <param name="message">Additional message or details related to the action.</param>
    public void LogAudit(string action, string description, string message)
    {
        // Create a new AuditLog entry
        string userName = User?.Identity?.Name ?? string.Empty;

        AuditLog auditLog = new AuditLog
        {
            Action = action,
            Details = $"{description}: {message}",
            Timestamp = DateTime.UtcNow,
            UserId = userName // Assuming the username is stored in the User.Identity.Name
        };

        // Add the audit log to the database context
        _dbContext.AuditLogs.Add(auditLog);

        // Save the audit log entry to the PostgreSQL database
        _dbContext.SaveChanges();
    }

    /// <summary>
    /// Logs a generic action, Serilog.
    /// </summary>
    /// <param name="action">The action being performed.</param>
    /// <param name="message">A message describing the action.</param>
    public void LogAction(string action, string message)
    {
        // Log to Serilog (console/file)
        _logger.LogInformation($"Action: {action}, Message: {message}");
    }

    /// <summary>
    /// Ensures that the current user is authorized for a specific role. Logs unauthorized attempts.
    /// </summary>
    /// <param name="role">The role that the user should have.</param>
    /// <returns>True if the user is authorized; otherwise, false.</returns>
    public bool EnsureUserIsAuthorized(string role)
    {
        if (!User.IsInRole(role))
        {
            // Log unauthorized access attempt via Serilog
            string userName = User?.Identity?.Name ?? "Unknown";
            _logger.LogWarning($"Unauthorized access attempt by user {userName} to role: {role}");

            // Log the unauthorized attempt to PostgreSQL
            LogAudit("Unauthorized Access", $"Access attempt to {role} by {userName}.", "Access denied.");

            SetAlert("You do not have the required permissions to perform this action.", "danger");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Validates the model state for errors and logs validation failures.
    /// </summary>
    /// <returns>True if the model state is valid; otherwise, false.</returns>
    public bool ValidateModelState()
    {
        if (!ModelState.IsValid)
        {
            // Log failed validation via Serilog
            _logger.LogWarning("Model validation failed for the form submission.");

            // Log validation failure to PostgreSQL for audit tracking
            LogAudit("Validation Error", "Model validation failed", "Validation errors present.");

            SetAlert("There are some validation errors. Please correct them and try again.", "danger");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Redirects to a specified action with an alert message in TempData.
    /// Logs the redirection action in Serilog.
    /// </summary>
    /// <param name="action">The action to redirect to.</param>
    /// <param name="controller">The controller to redirect to.</param>
    /// <param name="message">The message to show in the alert.</param>
    /// <param name="alertType">The type of alert to display (default is "success").</param>
    /// <param name="routeValues">An object containing the route parameters (optional).</param>
    /// <param name="area">The area to redirect to (optional).</param>
    /// <returns>A redirect to the specified action.</returns>
    public IActionResult RedirectWithAlert(string action, string controller, string message, string alertType = "success", object? routeValues = null, string? area = null)
    {
        // Set the alert message in TempData
        SetAlert(message, alertType);

        // Prepare the route values with area if provided
        RouteValueDictionary routeValuesWithArea = new RouteValueDictionary(routeValues ?? new object());
        if (!string.IsNullOrEmpty(area))
        {
            routeValuesWithArea["area"] = area;
        }

        // Log the redirection action to Serilog
        _logger.LogInformation($"Redirecting to action '{action}' in controller '{controller}' with route values: {string.Join(", ", routeValuesWithArea.Select(kvp => $"{kvp.Key}: {kvp.Value}"))}");

        // Perform the redirect
        return RedirectToAction(action, controller, routeValuesWithArea);
    }



    /// <summary>
    /// Sets the title for the page. This can be overridden in specific controllers or views.
    /// </summary>
    /// <param name="title">The title to be displayed on the page.</param>
    protected void SetPageTitle(string title)
    {
        ViewData["Title"] = title;
    }

    /// <summary>
    /// Retrieves a list of users based on their roles.
    /// </summary>
    /// <param name="roles">Roles as an enumerable, e.g. new[] { "User" }</param>
    /// <returns></returns>
    public async Task<List<ApplicationUser>> GetUsersByRolesAsync(IEnumerable<string> roles)
    {
        List<ApplicationUser> users = await (from user in _dbContext.Users
                                             join userRole in _dbContext.UserRoles on user.Id equals userRole.UserId
                                             join role in _dbContext.Roles on userRole.RoleId equals role.Id
                                             where roles.Contains(role.Name)
                                             select user).ToListAsync();

        return users;
    }
}
