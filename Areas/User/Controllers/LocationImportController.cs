using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Quartz;
using Wayfarer.Jobs;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Models.ViewModels;

namespace Wayfarer.Areas.User.Controllers
{
    [Area("User")]
    [Authorize(Roles = "User")]
    public class LocationImportController : BaseController
    {
        /// <summary>
        /// Quartz job group for location import jobs.
        /// </summary>
        private const string ImportJobGroup = "Imports";

        private readonly IWebHostEnvironment _environment;
        private readonly IScheduler        _scheduler;

        public LocationImportController(ApplicationDbContext dbContext,
            ILogger<LocationImportController> logger,
            IWebHostEnvironment environment,
            IScheduler scheduler)
            : base(logger, dbContext)
        {
            _environment = environment;
            _scheduler = scheduler;
        }

        /// <summary>
        /// Lists all location data import jobs with statuses of PENDING, INPROGRESS, COMPLETED, FAILED 
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            var imports = await _dbContext.LocationImports
                .Where(x => x.UserId == userId)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();
            
            ViewData["UserId"] = userId;

            SetPageTitle("Location Imports");
            return View(imports);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartImport(int id)
        {
            var import = await _dbContext.LocationImports
                .FirstOrDefaultAsync(x => x.Id == id);

            if (import == null)
            {
                SetAlert("Import record not found.", "danger");
                return RedirectToAction("Index");
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (import.UserId != userId)
            {
                SetAlert("Unauthorized access.", "danger");
                return RedirectToAction("Index");
            }

            if (import.Status == ImportStatus.InProgress)
            {
                SetAlert("Import job is already in progress.", "warning");
                return RedirectToAction("Index");
            }

            import.Status = ImportStatus.InProgress;
            await _dbContext.SaveChangesAsync();

            // Start the Quartz job
            await StartImportJob(import);

            SetAlert("Import started successfully.");
            return RedirectToAction("Index");
        }

        private async Task StartImportJob(LocationImport import)
        {
            var jobKey = new JobKey($"LocationImportJob_{import.Id}", ImportJobGroup);

            _logger.LogInformation("Attempting to schedule LocationImportJob for import ID {ImportId}", import.Id);

            var jobDetail = JobBuilder.Create<LocationImportJob>()
                .WithIdentity(jobKey)
                .UsingJobData("importId", import.Id.ToString())
                .StoreDurably()  // Ensures the job is recoverable after a restart
                .Build();

            var trigger = TriggerBuilder.Create()
                .WithIdentity($"LocationImportTrigger_{import.Id}", ImportJobGroup)
                .StartNow()
                .Build();
            
            if (await _scheduler.CheckExists(jobKey))
            {
                _logger.LogWarning("Job with key {JobKey} already exists. Deleting existing job before rescheduling.",
                    jobKey);
                await _scheduler.DeleteJob(jobKey);
            }

            await _scheduler.ScheduleJob(jobDetail, trigger);
            _logger.LogInformation("Successfully scheduled LocationImportJob for import ID {ImportId}", import.Id);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StopImport(int id)
        {
            var import = await _dbContext.LocationImports
                .FirstOrDefaultAsync(x => x.Id == id);

            if (import == null)
            {
                SetAlert("Import record not found.", "danger");
                return RedirectToAction("Index");
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (import.UserId != userId)
            {
                SetAlert("Unauthorized access.", "danger");
                return RedirectToAction("Index");
            }

            if (import.Status != ImportStatus.InProgress)
            {
                SetAlert("No import job is currently in progress to stop.", "warning");
                return RedirectToAction("Index");
            }

            // Set status to Stopping
            import.Status = ImportStatus.Stopping;
            await _dbContext.SaveChangesAsync();

            // Implement logic to stop the job (e.g., background task cancellation)
            await StopImportJob(import);

            SetAlert("Import stopping request submitted successfully.");
            return RedirectToAction("Index");
        }

        private async Task StopImportJob(LocationImport import)
        {
            var jobKey = new JobKey($"LocationImportJob_{import.Id}", ImportJobGroup);

            _logger.LogInformation("Attempting to stop job with key {JobKey}", jobKey);
            
            if (await _scheduler.CheckExists(jobKey))
            {
                var job = await _scheduler.GetJobDetail(jobKey);

                // Mark the job status to stopping
                import.Status = ImportStatus.Stopping;
                await _dbContext.SaveChangesAsync();

                _logger.LogInformation("Job with key {JobKey} is in progress. Attempting to interrupt it.", jobKey);
                await _scheduler.Interrupt(jobKey);

                // Optionally, delete the job after interrupting
                await _scheduler.DeleteJob(jobKey);

                import.Status = ImportStatus.Stopped;
                await _dbContext.SaveChangesAsync();
            }
            else
            {
                _logger.LogWarning("Job with key {JobKey} does not exist.", jobKey);
            }

            SetAlert("Import stopping request submitted successfully.");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var import = await _dbContext.LocationImports
                .FirstOrDefaultAsync(x => x.Id == id);

            if (import == null)
            {
                SetAlert("Upload record not found.", "danger");
                return RedirectToAction("Index");
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (import.UserId != userId)
            {
                SetAlert("Unauthorized access.", "danger");
                return RedirectToAction("Index");
            }

            // Do not allow deletion if the import is currently in progress
            if (import.Status == ImportStatus.InProgress)
            {
                SetAlert("Upload is currently in progress and cannot be canceled or removed.", "warning");
                return RedirectToAction("Index");
            }

            try
            {
                // Attempt to delete the file if it exists
                if (System.IO.File.Exists(import.FilePath))
                {
                    System.IO.File.Delete(import.FilePath);
                }

                _dbContext.LocationImports.Remove(import);
                await _dbContext.SaveChangesAsync();

                SetAlert("Upload record removed successfully.");
            }
            catch (Exception ex)
            {
                HandleError(ex);
                SetAlert("An error occurred while deleting the import record.", "danger");
            }

            return RedirectToAction("Index");
        }

        [HttpGet]
        public IActionResult Upload()
        {
            var fileTypes = Enum.GetValues(typeof(LocationImportFileType))
                .Cast<LocationImportFileType>()
                .Select(fileType => new SelectListItem
                {
                    Value = fileType.ToString(),
                    Text = $"{fileType} ({string.Join(", ", fileType.GetAllowedExtensions())})"
                })
                .ToList();

            fileTypes.Insert(0, new SelectListItem { Value = "", Text = "-- Select File Type --" });
            ViewBag.FileTypes = fileTypes;

            var acceptedExtensions = Enum.GetValues(typeof(LocationImportFileType))
                .Cast<LocationImportFileType>()
                .SelectMany(fileType => fileType.GetAllowedExtensions())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            ViewBag.AcceptedExtensions = string.Join(",", acceptedExtensions);

            ViewBag.UploadLimit = _dbContext.ApplicationSettings.First().UploadSizeLimitMB.ToString();

            SetPageTitle("Upload File");
            return View(new LocationImportUploadViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(LocationImportUploadViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return RedirectToAction("Upload");
            }

            if (model.File == null || model.File.Length == 0)
            {
                ModelState.AddModelError("", "Please select a valid file.");
                return View("Upload", model);
            }

            if (!model.FileType.HasValue)
            {
                ModelState.AddModelError(nameof(model.FileType), "Please select a valid file type.");
                return View("Upload", model);
            }

            var extension = Path.GetExtension(model.File.FileName);
            if (!model.FileType.Value.IsExtensionValid(extension))
            {
                ModelState.AddModelError(nameof(model.File),
                    $"Invalid file extension '{extension}'. Allowed: {string.Join(", ", model.FileType.Value.GetAllowedExtensions())}");
                return View("Upload", model);
            }

            if (!ValidateModelState())
            {
                return View("Upload", model);
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
            var randomString = Guid.NewGuid().ToString("N").Substring(0, 6);
            var uniqueFileName = $"{model.FileType}_{userId}_Timestamp_{timestamp}__{randomString}";

            var uploadDirectory = Path.Combine(_environment.ContentRootPath, "Uploads", "Temp");
            Directory.CreateDirectory(uploadDirectory);

            var filePath = Path.Combine(uploadDirectory, uniqueFileName);
            _logger.LogInformation($"Uploading {uniqueFileName} to {uploadDirectory} with file path {filePath}");

            try
            {
                using (var stream = new FileStream(filePath, FileMode.Create))
                {
                    await model.File.CopyToAsync(stream);
                }

                var importRecord = new LocationImport
                {
                    UserId = userId,
                    FileType = model.FileType.Value,
                    FilePath = filePath,
                    CreatedAt = DateTime.UtcNow,
                    UpdatedAt = DateTime.UtcNow,
                    LastProcessedIndex = 0,
                    TotalRecords = 0,
                    Status = ImportStatus.Stopped,
                    ErrorMessage = null
                };

                _dbContext.LocationImports.Add(importRecord);
                await _dbContext.SaveChangesAsync();

                SetAlert("File uploaded successfully and is pending import.");
            }
            catch (Exception ex)
            {
                HandleError(ex);
                return RedirectToAction("Index");
            }

            return RedirectToAction("Index");
        }
    }
}
