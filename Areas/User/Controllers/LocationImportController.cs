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
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationImports;

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
        private readonly IImportEnrichmentHandoff? _enrichmentHandoff;
        private readonly ILocationImportLifecycle _importLifecycle;

        public LocationImportController(ApplicationDbContext dbContext,
            ILogger<LocationImportController> logger,
            IWebHostEnvironment environment,
            IScheduler scheduler,
            IImportEnrichmentHandoff? enrichmentHandoff = null,
            IWorkflowScheduleProjection? workflowProjection = null,
            ILocationImportLifecycle? importLifecycle = null)
            : base(logger, dbContext)
        {
            _environment = environment;
            _scheduler = scheduler;
            _enrichmentHandoff = enrichmentHandoff;
            _importLifecycle = importLifecycle ?? new LocationImportLifecycle(
                dbContext, scheduler, logger as ILogger<LocationImportLifecycle>
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LocationImportLifecycle>.Instance);
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
            var workflow = await _dbContext.LocationEnrichmentWorkflows.AsNoTracking()
                .SingleOrDefaultAsync(item => item.UserId == userId);
            ViewData["EnrichmentPresentation"] = LocationEnrichmentPresentation.Build(workflow);

            SetPageTitle("Location Imports");
            return View(imports);
        }

        /// <summary>Starts or idempotently reuses the authenticated user's one enrichment workflow.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> StartEnrichment()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            if (_enrichmentHandoff is null) return Conflict("control-unavailable");
            var result = await _enrichmentHandoff.StartAsync(userId);
            return result.Succeeded ? RedirectToAction(nameof(Index)) : Conflict(result.Code);
        }

        /// <summary>Persists authenticated pause intent before neutralizing Quartz work.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public Task<IActionResult> PauseEnrichment() => ExecuteEnrichmentAsync(
            (owner, userId) => owner.PauseAsync(userId));

        /// <summary>Idempotently resumes the authenticated user's current nonterminal epoch.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public Task<IActionResult> ResumeEnrichment() => ExecuteEnrichmentAsync(
            (owner, userId) => owner.ResumeAsync(userId));

        /// <summary>Cancels only enrichment metadata while retaining import and Location data.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public Task<IActionResult> CancelEnrichment() => ExecuteEnrichmentAsync(
            (owner, userId) => owner.CancelAsync(userId));

        /// <summary>Retries deferred work only for the authenticated user's current provider generation.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RetryDeferredEnrichment()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            if (_enrichmentHandoff is null) return Conflict("control-unavailable");
            var result = await _enrichmentHandoff.RetryDeferredAsync(userId);
            return result.Succeeded ? RedirectToAction(nameof(Index)) : Conflict(result.Code);
        }

        private async Task<IActionResult> ExecuteEnrichmentAsync(
            Func<IImportEnrichmentHandoff, string, Task<EnrichmentCommandResult>> execute)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            if (_enrichmentHandoff is null) return Conflict("control-unavailable");
            var result = await execute(_enrichmentHandoff, userId);
            return result.Succeeded ? RedirectToAction(nameof(Index)) : Conflict(result.Code);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartImport(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            var result = await _importLifecycle.StartAsync(userId, id, HttpContext.RequestAborted);
            SetAlert(result.Code switch
            {
                LocationImportCommandCode.Accepted => "Import started successfully.",
                LocationImportCommandCode.ProjectionPending => "Import accepted and awaiting scheduler recovery.",
                LocationImportCommandCode.InvalidState => "Import cannot be started in its current state.",
                _ => "Import record not found."
            }, result.Succeeded ? "success" : "warning");
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StopImport(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            var result = await _importLifecycle.StopAsync(userId, id, HttpContext.RequestAborted);
            SetAlert(result.Succeeded ? "Import stopping request submitted successfully." : "No active import was found.",
                result.Succeeded ? "success" : "warning");
            return RedirectToAction("Index");
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            var result = await _importLifecycle.DeleteAsync(userId, id, HttpContext.RequestAborted);
            SetAlert(result.Code == LocationImportCommandCode.Accepted
                ? "Upload record removed successfully."
                : result.Code == LocationImportCommandCode.ExecutionActive
                    ? "Upload is active or stopping and cannot be removed yet."
                    : "Upload record not found.", result.Code == LocationImportCommandCode.Accepted ? "success" : "warning");
            return RedirectToAction("Index");
        }

        [HttpGet]
        public IActionResult Upload()
        {
            PrepareUploadView();
            SetPageTitle("Upload File");
            return View(new LocationImportUploadViewModel());
        }

        /// <summary>Builds upload choices for GET and validation rerenders.</summary>
        private void PrepareUploadView()
        {
            var fileTypes = Enum.GetValues(typeof(LocationImportFileType))
                .Cast<LocationImportFileType>()
                .Where(fileType => fileType.IsSupportedUpload())
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
                .Where(fileType => fileType.IsSupportedUpload())
                .SelectMany(fileType => fileType.GetAllowedExtensions())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            ViewBag.AcceptedExtensions = string.Join(",", acceptedExtensions);

            var uploadSettings = _dbContext.ApplicationSettings.OrderBy(s => s.Id).FirstOrDefault();
            ViewBag.UploadLimit = (uploadSettings?.UploadSizeLimitMB ?? ApplicationSettings.DefaultUploadSizeLimitMB).ToString();
        }

        /// <summary>Preserves the submitted model and required view data after validation failure.</summary>
        private ViewResult InvalidUpload(LocationImportUploadViewModel model)
        {
            PrepareUploadView();
            SetPageTitle("Upload File");
            return View("Upload", model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Upload(LocationImportUploadViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return InvalidUpload(model);
            }

            if (model.File == null || model.File.Length == 0)
            {
                ModelState.AddModelError("", "Please select a valid file.");
                return InvalidUpload(model);
            }

            if (!model.FileType.HasValue)
            {
                ModelState.AddModelError(nameof(model.FileType), "Please select a valid file type.");
                return InvalidUpload(model);
            }

            if (!model.FileType.Value.IsSupportedUpload())
            {
                ModelState.AddModelError(nameof(model.FileType),
                    "Generic GeoJSON is not a supported location-history format. Select Wayfarer GeoJSON for Wayfarer exports.");
                return InvalidUpload(model);
            }

            var extension = Path.GetExtension(model.File.FileName);
            if (!model.FileType.Value.IsExtensionValid(extension))
            {
                ModelState.AddModelError(nameof(model.File),
                    $"Invalid file extension '{extension}'. Allowed: {string.Join(", ", model.FileType.Value.GetAllowedExtensions())}");
                return InvalidUpload(model);
            }

            if (!ValidateModelState())
            {
                return InvalidUpload(model);
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
                    ,EnrichmentRequested = model.EnrichmentRequested
                    ,EnrichmentRequestedAtUtc = model.EnrichmentRequested ? DateTime.UtcNow : null
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
