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
            if (currentUser == null)
            {
                SetAlert("User not found.", "danger");
                return RedirectToAction("Index", "Home");
            }

            ViewData["Username"] = currentUser.UserName ?? string.Empty;
            
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
                model.CustomThreshold = string.Empty;  // Optionally clear the value
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
        /// Returns whether prev/next navigation is available based ONLY on future date restrictions.
        /// Always allows navigation to past dates to prevent users from getting trapped in dates with no data.
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

                // Initialize all navigation flags - default to true (allow navigation)
                bool canNavigatePrevDay = true, canNavigateNextDay = false;
                bool canNavigatePrevMonth = true, canNavigateNextMonth = false;
                bool canNavigatePrevYear = true, canNavigateNextYear = false;

                // Check day navigation (only relevant in day view)
                if (dateType.ToLower() == "day" && month.HasValue && day.HasValue)
                {
                    var currentDate = new DateTime(year, month.Value, day.Value);
                    var nextDate = currentDate.AddDays(1);

                    // Can't navigate to future dates
                    canNavigateNextDay = nextDate.Date <= now.Date;
                }

                // Check month navigation (relevant in day and month views)
                if ((dateType.ToLower() == "day" || dateType.ToLower() == "month") && month.HasValue)
                {
                    int nextMonth = month.Value == 12 ? 1 : month.Value + 1;
                    int nextMonthYear = month.Value == 12 ? year + 1 : year;

                    if (dateType.ToLower() == "day" && day.HasValue)
                    {
                        // Check if next month would be in the future
                        int currentDay = day.Value;
                        var nextMonthDate = new DateTime(nextMonthYear, nextMonth, Math.Min(currentDay, DateTime.DaysInMonth(nextMonthYear, nextMonth)));
                        canNavigateNextMonth = nextMonthDate.Date <= now.Date;
                    }
                    else // month view
                    {
                        // Can't navigate to future months
                        canNavigateNextMonth = (nextMonthYear < now.Year) || (nextMonthYear == now.Year && nextMonth <= now.Month);
                    }
                }

                // Check year navigation (always relevant, maintains month/day context)
                {
                    int nextYearVal = year + 1;

                    if (dateType.ToLower() == "day" && month.HasValue && day.HasValue)
                    {
                        // Check if next year would be in the future
                        int currentDay = day.Value;
                        var nextYearDate = new DateTime(nextYearVal, month.Value, Math.Min(currentDay, DateTime.DaysInMonth(nextYearVal, month.Value)));
                        canNavigateNextYear = nextYearDate.Date <= now.Date;
                    }
                    else if (dateType.ToLower() == "month" && month.HasValue)
                    {
                        // Can't navigate to future years
                        canNavigateNextYear = (nextYearVal < now.Year) || (nextYearVal == now.Year && month.Value <= now.Month);
                    }
                    else // year view
                    {
                        // Can't navigate to future years
                        canNavigateNextYear = nextYearVal <= now.Year;
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

        /// <summary>
        /// Get detailed statistics for a specific chronological period (day, month, or year).
        /// Returns location count and detailed arrays of countries, regions, cities for the selected period.
        /// </summary>
        /// <param name="dateType">Type of period: "day", "month", or "year"</param>
        /// <param name="year">Year</param>
        /// <param name="month">Month (1-12)</param>
        /// <param name="day">Day (1-31)</param>
        [HttpGet]
        public async Task<IActionResult> GetChronologicalStatsDetailed(string dateType, int year, int? month = null, int? day = null)
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

                var detailedStats = await _statsService.GetDetailedStatsForDateRangeAsync(currentUser.Id, startDate, endDate);

                return Ok(new
                {
                    success = true,
                    stats = detailedStats
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting detailed chronological stats");
                return StatusCode(500, new { success = false, message = "An error occurred while fetching detailed stats." });
            }
        }
    }
}
