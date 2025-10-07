using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Wayfarer.Models;
using Wayfarer.Models.ViewModels;
using Wayfarer.Parsers;
using Wayfarer.Util;

namespace Wayfarer.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class TimelineController : BaseController
    {
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly Parsers.LocationService _locationService;
        private readonly ILocationStatsService _statsService;

        public TimelineController(
            ILogger<BaseController> logger,
            ApplicationDbContext dbContext,
            UserManager<ApplicationUser> userManager,
            Parsers.LocationService locationService,
            ILocationStatsService statsService)
            : base(logger, dbContext)
        {
            _userManager = userManager ?? throw new ArgumentNullException(nameof(userManager));
            _locationService = locationService ?? throw new ArgumentNullException(nameof(locationService));
            _statsService = statsService ?? throw new ArgumentNullException(nameof(statsService));
        }

        // Action to display the timeline for the logged-in user
        public async Task<IActionResult> Index()
        {
            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);
            ViewData["Username"] = currentUser.UserName;
            
            SetPageTitle("Private Timeline");
            return View();
        }

        // GET: Timeline/Settings
        public async Task<IActionResult> Settings()
        {
            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            if (currentUser == null)
            {
                SetAlert("User not found.", "danger");
                return RedirectToAction("Index", "Home");
            }

            TimelineSettingsViewModel model = new TimelineSettingsViewModel
            {
                IsTimelinePublic = currentUser.IsTimelinePublic,
                PublicTimelineTimeThreshold = currentUser.PublicTimelineTimeThreshold
            };

            SetPageTitle("Timeline Settings");
            return View(model);
        }

        // POST: Timeline/Settings
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdateSettings(TimelineSettingsViewModel model)
        {
            if (model.PublicTimelineTimeThreshold != "custom")
            {
                model.CustomThreshold = null;  // Optionally clear the value
                ModelState.Remove("CustomThreshold");  // Skip validation for CustomThreshold
            }

            if (!ValidateModelState())
            {
                return View("Settings", model);
            }

            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);

            if (currentUser == null)
            {
                SetAlert("User not found.", "danger");
                return RedirectToAction("Index", "Home");
            }

            try
            {
                currentUser.IsTimelinePublic = model.IsTimelinePublic;

                // If the value is "custom", use the custom input
                if (model.PublicTimelineTimeThreshold == "custom" && !string.IsNullOrEmpty(model.CustomThreshold))
                {
                    if (!TimespanHelper.IsValidThreshold(model.CustomThreshold))
                    {
                        ModelState.AddModelError("CustomThreshold", "Invalid time threshold format. Use values like 1d, 2.5w, 0.1h, etc.");
                        return View("Settings", model);
                    }
                    currentUser.PublicTimelineTimeThreshold = model.CustomThreshold; // Save custom input
                }
                else
                {
                    currentUser.PublicTimelineTimeThreshold = model.PublicTimelineTimeThreshold;
                }

                _dbContext.Users.Update(currentUser);
                await _dbContext.SaveChangesAsync();

                SetAlert("Timeline settings updated successfully.");
                LogAction("TimelineSettingsUpdate", $"User {currentUser.UserName} updated their timeline settings.");
                return RedirectToAction("Settings");
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return View("Settings", model);
            }
        }

        /// <summary>
        /// Display the chronological timeline view for the logged-in user.
        /// This view allows navigation by day, month, or year.
        /// </summary>
        public async Task<IActionResult> Chronological()
        {
            ApplicationUser? currentUser = await _userManager.GetUserAsync(User);
            if (currentUser == null)
            {
                SetAlert("User not found.", "danger");
                return RedirectToAction("Index", "Home");
            }

            ViewData["Username"] = currentUser.UserName;
            SetPageTitle("Chronological Timeline");
            return View();
        }

        /// <summary>
        /// Get chronological location data for the logged-in user.
        /// Supports day, month, and year filtering.
        /// </summary>
        /// <param name="dateType">Type of period: "day", "month", or "year"</param>
        /// <param name="year">Year to filter</param>
        /// <param name="month">Month to filter (1-12)</param>
        /// <param name="day">Day to filter (1-31)</param>
        [HttpGet]
        public async Task<IActionResult> GetChronologicalData(string dateType, int year, int? month = null, int? day = null)
        {
            try
            {
                ApplicationUser? currentUser = await _userManager.GetUserAsync(User);
                if (currentUser == null)
                {
                    return Unauthorized(new { success = false, message = "User not found." });
                }

                var (locations, totalItems) = await _locationService.GetLocationsByDateAsync(
                    currentUser.Id, dateType, year, month, day, CancellationToken.None);

                return Ok(new
                {
                    success = true,
                    data = locations,
                    totalItems = totalItems,
                    dateType = dateType,
                    year = year,
                    month = month,
                    day = day
                });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { success = false, message = ex.Message });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chronological data");
                return StatusCode(500, new { success = false, message = "An error occurred while fetching data." });
            }
        }

        /// <summary>
        /// Check if user has data for a specific date.
        /// Used for conditional prev/next date navigation.
        /// </summary>
        /// <param name="date">Date to check (ISO format)</param>
        [HttpGet]
        public async Task<IActionResult> HasDataForDate(string date)
        {
            try
            {
                ApplicationUser? currentUser = await _userManager.GetUserAsync(User);
                if (currentUser == null)
                {
                    return Unauthorized(new { hasData = false });
                }

                if (!DateTime.TryParse(date, out DateTime parsedDate))
                {
                    return BadRequest(new { hasData = false, message = "Invalid date format." });
                }

                bool hasData = await _locationService.HasDataForDateAsync(
                    currentUser.Id, parsedDate, CancellationToken.None);

                return Ok(new { hasData = hasData });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking data availability for date");
                return StatusCode(500, new { hasData = false });
            }
        }

        /// <summary>
        /// Get statistics for a specific chronological period (day, month, or year).
        /// Returns location count, countries, regions, cities for the selected period.
        /// </summary>
        /// <param name="dateType">Type of period: "day", "month", or "year"</param>
        /// <param name="year">Year</param>
        /// <param name="month">Month (1-12)</param>
        /// <param name="day">Day (1-31)</param>
        [HttpGet]
        public async Task<IActionResult> GetChronologicalStats(string dateType, int year, int? month = null, int? day = null)
        {
            try
            {
                ApplicationUser? currentUser = await _userManager.GetUserAsync(User);
                if (currentUser == null)
                {
                    return Unauthorized(new { success = false, message = "User not found." });
                }

                // Build date range based on dateType
                DateTime startDate, endDate;
                switch (dateType.ToLower())
                {
                    case "day":
                        if (!month.HasValue || !day.HasValue)
                            return BadRequest(new { success = false, message = "Month and day are required for day filter" });
                        startDate = new DateTime(year, month.Value, day.Value, 0, 0, 0, DateTimeKind.Utc);
                        endDate = DateTime.SpecifyKind(startDate.AddDays(1).AddTicks(-1), DateTimeKind.Utc);
                        break;

                    case "month":
                        if (!month.HasValue)
                            return BadRequest(new { success = false, message = "Month is required for month filter" });
                        startDate = new DateTime(year, month.Value, 1, 0, 0, 0, DateTimeKind.Utc);
                        endDate = DateTime.SpecifyKind(startDate.AddMonths(1).AddTicks(-1), DateTimeKind.Utc);
                        break;

                    case "year":
                        startDate = new DateTime(year, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                        endDate = DateTime.SpecifyKind(new DateTime(year, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(9999999), DateTimeKind.Utc);
                        break;

                    default:
                        return BadRequest(new { success = false, message = $"Invalid dateType: {dateType}" });
                }

                var stats = await _statsService.GetStatsForDateRangeAsync(currentUser.Id, startDate, endDate);

                return Ok(new
                {
                    success = true,
                    stats = stats
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting chronological stats");
                return StatusCode(500, new { success = false, message = "An error occurred while fetching stats." });
            }
        }

        /// <summary>
        /// Check navigation availability for chronological timeline.
        /// Returns whether prev/next navigation is available for day/month/year based on data and current date.
        /// Handles contextual navigation (e.g., year navigation in month view maintains the month).
        /// </summary>
        /// <param name="dateType">Type of period: "day", "month", or "year"</param>
        /// <param name="year">Current year</param>
        /// <param name="month">Current month (1-12)</param>
        /// <param name="day">Current day (1-31)</param>
        [HttpGet]
        public async Task<IActionResult> CheckNavigationAvailability(string dateType, int year, int? month = null, int? day = null)
        {
            try
            {
                ApplicationUser? currentUser = await _userManager.GetUserAsync(User);
                if (currentUser == null)
                {
                    return Unauthorized(new { success = false });
                }

                var now = DateTime.Now;

                // Initialize all navigation flags
                bool canNavigatePrevDay = false, canNavigateNextDay = false;
                bool canNavigatePrevMonth = false, canNavigateNextMonth = false;
                bool canNavigatePrevYear = false, canNavigateNextYear = false;

                // Check day navigation (only relevant in day view)
                if (dateType.ToLower() == "day" && month.HasValue && day.HasValue)
                {
                    var currentDate = new DateTime(year, month.Value, day.Value);
                    var prevDate = currentDate.AddDays(-1);
                    var nextDate = currentDate.AddDays(1);

                    canNavigatePrevDay = await _locationService.HasDataForDateAsync(currentUser.Id, prevDate, CancellationToken.None);

                    // Can't navigate to future dates
                    if (nextDate.Date <= now.Date)
                    {
                        canNavigateNextDay = await _locationService.HasDataForDateAsync(currentUser.Id, nextDate, CancellationToken.None);
                    }
                }

                // Check month navigation (relevant in day and month views)
                if ((dateType.ToLower() == "day" || dateType.ToLower() == "month") && month.HasValue)
                {
                    // For day view, preserve day when navigating months
                    int currentDay = day ?? 1;

                    int prevMonth = month.Value == 1 ? 12 : month.Value - 1;
                    int prevMonthYear = month.Value == 1 ? year - 1 : year;
                    int nextMonth = month.Value == 12 ? 1 : month.Value + 1;
                    int nextMonthYear = month.Value == 12 ? year + 1 : year;

                    if (dateType.ToLower() == "day")
                    {
                        // Check if specific day exists in prev/next month
                        var prevMonthDate = new DateTime(prevMonthYear, prevMonth, Math.Min(currentDay, DateTime.DaysInMonth(prevMonthYear, prevMonth)));
                        var nextMonthDate = new DateTime(nextMonthYear, nextMonth, Math.Min(currentDay, DateTime.DaysInMonth(nextMonthYear, nextMonth)));

                        canNavigatePrevMonth = await _locationService.HasDataForDateAsync(currentUser.Id, prevMonthDate, CancellationToken.None);

                        if (nextMonthDate.Date <= now.Date)
                        {
                            canNavigateNextMonth = await _locationService.HasDataForDateAsync(currentUser.Id, nextMonthDate, CancellationToken.None);
                        }
                    }
                    else // month view
                    {
                        canNavigatePrevMonth = await _locationService.HasDataForMonthAsync(currentUser.Id, prevMonthYear, prevMonth, CancellationToken.None);

                        // Can't navigate to future months
                        if (nextMonthYear < now.Year || (nextMonthYear == now.Year && nextMonth <= now.Month))
                        {
                            canNavigateNextMonth = await _locationService.HasDataForMonthAsync(currentUser.Id, nextMonthYear, nextMonth, CancellationToken.None);
                        }
                    }
                }

                // Check year navigation (always relevant, maintains month/day context)
                {
                    int prevYearVal = year - 1;
                    int nextYearVal = year + 1;

                    if (dateType.ToLower() == "day" && month.HasValue && day.HasValue)
                    {
                        // Check if specific day exists in prev/next year
                        int currentDay = day.Value;
                        var prevYearDate = new DateTime(prevYearVal, month.Value, Math.Min(currentDay, DateTime.DaysInMonth(prevYearVal, month.Value)));
                        var nextYearDate = new DateTime(nextYearVal, month.Value, Math.Min(currentDay, DateTime.DaysInMonth(nextYearVal, month.Value)));

                        canNavigatePrevYear = await _locationService.HasDataForDateAsync(currentUser.Id, prevYearDate, CancellationToken.None);

                        if (nextYearDate.Date <= now.Date)
                        {
                            canNavigateNextYear = await _locationService.HasDataForDateAsync(currentUser.Id, nextYearDate, CancellationToken.None);
                        }
                    }
                    else if (dateType.ToLower() == "month" && month.HasValue)
                    {
                        // Check if same month exists in prev/next year
                        canNavigatePrevYear = await _locationService.HasDataForMonthAsync(currentUser.Id, prevYearVal, month.Value, CancellationToken.None);

                        // Can't navigate to future years
                        if (nextYearVal < now.Year || (nextYearVal == now.Year && month.Value <= now.Month))
                        {
                            canNavigateNextYear = await _locationService.HasDataForMonthAsync(currentUser.Id, nextYearVal, month.Value, CancellationToken.None);
                        }
                    }
                    else // year view
                    {
                        canNavigatePrevYear = await _locationService.HasDataForYearAsync(currentUser.Id, prevYearVal, CancellationToken.None);

                        // Can't navigate to future years
                        if (nextYearVal <= now.Year)
                        {
                            canNavigateNextYear = await _locationService.HasDataForYearAsync(currentUser.Id, nextYearVal, CancellationToken.None);
                        }
                    }
                }

                return Ok(new
                {
                    success = true,
                    canNavigatePrevDay,
                    canNavigateNextDay,
                    canNavigatePrevMonth,
                    canNavigateNextMonth,
                    canNavigatePrevYear,
                    canNavigateNextYear
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking navigation availability");
                return StatusCode(500, new { success = false });
            }
        }
    }
}