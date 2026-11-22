using Microsoft.EntityFrameworkCore;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Quartz;
using Wayfarer.Localization;
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
        private readonly LocationImportStagedFiles _stagedFiles;
        private readonly WebMessages _text;
        private readonly IImportEnrichmentHandoff? _enrichmentHandoff;
        private readonly ILocationImportLifecycle _importLifecycle;
        private readonly ILocationEnrichmentPresentationProjector _enrichmentPresentation;

        /// <summary>Uses the shared staging authority for new uploads and lifecycle cleanup.</summary>
        public LocationImportController(LocationImportStagedFiles stagedFiles, ApplicationDbContext dbContext,
            ILogger<LocationImportController> logger,
            IWebHostEnvironment environment,
            IScheduler scheduler,
            ILocationEnrichmentPresentationProjector enrichmentPresentation, WebMessages text,
            IImportEnrichmentHandoff? enrichmentHandoff = null,
            IWorkflowScheduleProjection? workflowProjection = null,
            ILocationImportLifecycle? importLifecycle = null,
            IDbContextFactory<ApplicationDbContext>? contextFactory = null)
            : base(logger, dbContext)
        {
            _stagedFiles = stagedFiles;
            _text = text;
            _enrichmentPresentation = enrichmentPresentation;
            _enrichmentHandoff = enrichmentHandoff;
            _importLifecycle = importLifecycle ?? new LocationImportLifecycle(
                stagedFiles, contextFactory ?? throw new ArgumentNullException(nameof(contextFactory)), scheduler,
                logger as ILogger<LocationImportLifecycle>
                    ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<LocationImportLifecycle>.Instance);
        }

        /// <summary>
        /// Lists all location data import jobs with statuses of PENDING, INPROGRESS, COMPLETED, FAILED 
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Index()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            var imports = await _dbContext.LocationImports
                .Where(x => x.UserId == userId)
                .OrderByDescending(x => x.CreatedAt)
                .ToListAsync();
            
            // Managed file state is persisted custody, never a filesystem inference or legacy sweep.
            ViewData["ManagedFiles"] = await _dbContext.Set<MobileLocationImportUpload>()
                .Where(row => row.UserId == userId && row.ImportId != null)
                .ToDictionaryAsync(row => row.ImportId!.Value, row => row.FileState);
            ViewData["EnrichmentPresentation"] = await _enrichmentPresentation.ProjectAsync(
                userId, HttpContext.RequestAborted);

            return View(imports);
        }

        /// <summary>Starts or idempotently reuses the authenticated user's one enrichment workflow.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public Task<IActionResult> StartEnrichment() => ExecuteEnrichmentAsync(
            (owner, userId) => owner.StartAsync(userId));

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
            if (_enrichmentHandoff is null) return EnrichmentConflict("control-unavailable");
            var result = await _enrichmentHandoff.RetryDeferredAsync(userId);
            var feedback = RetryFeedback(result);
            SetEnrichmentAlert(feedback, result.AffectedCount);
            return RedirectToAction(nameof(Index));
        }

        /// <summary>Schedules explicit missing-locality repair for the authenticated user's eligible records.</summary>
        [HttpPost, ValidateAntiForgeryToken]
        public async Task<IActionResult> RepairIncompleteAddresses()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            if (_enrichmentHandoff is null) return EnrichmentConflict("control-unavailable");
            var result = await _enrichmentHandoff.RepairIncompleteAsync(userId);
            var feedback = result switch
            {
                { Classification: LocationEnrichmentCommandResult.Applied, AffectedCount: > 0 } =>
                    ($"Repair scheduled for {result.AffectedCount} incomplete locations.", "success", "Transfers.RepairScheduled", true),
                { Classification: LocationEnrichmentCommandResult.AlreadySatisfied, Code: "nothing-to-repair" } =>
                    ("No incomplete provider addresses are currently eligible.", "info", "Transfers.NoRepair", false),
                { Classification: LocationEnrichmentCommandResult.SchedulingPending, AffectedCount: > 0 } =>
                    ($"Repair was saved for {result.AffectedCount} incomplete locations; scheduling requires reconciliation.", "warning", "Transfers.RepairPending", true),
                { Code: "authority-unavailable" } =>
                    ("Current Geoapify geocoding authority is required before repairing incomplete addresses.", "warning", "Transfers.RepairAuthority", false),
                _ => ("Incomplete addresses could not be scheduled because the workflow changed. Refresh and try again.", "warning", "Transfers.RepairChanged", false)
            };
            SetEnrichmentAlert(feedback, result.AffectedCount);
            return RedirectToAction(nameof(Index));
        }

        /// <summary>Maps finite retry outcomes to credential-free user feedback.</summary>
        private static (string Message, string Type, string Key, bool Count) RetryFeedback(EnrichmentCommandResult result) => result switch
        {
            { Classification: LocationEnrichmentCommandResult.Applied, AffectedCount: > 0 } =>
                ($"Retry scheduled for {result.AffectedCount} deferred locations.", "success", "Transfers.RetryScheduled", true),
            { Classification: LocationEnrichmentCommandResult.AlreadySatisfied, Code: "nothing-to-retry" } =>
                ("No deferred locations are currently eligible for retry.", "info", "Transfers.NoRetry", false),
            { Classification: LocationEnrichmentCommandResult.SchedulingPending, AffectedCount: > 0 } =>
                ($"Retry was saved for {result.AffectedCount} deferred locations; scheduling requires reconciliation.", "warning", "Transfers.RetryPending", true),
            { Code: "authority-unavailable" } =>
                ("The current geocoding provider must be available before retrying.", "warning", "Transfers.RetryAuthority", false),
            { Code: "invalid-state" } =>
                ("The enrichment workflow changed state before retry could start.", "warning", "Transfers.RetryStateChanged", false),
            _ => ("Deferred enrichment could not be retried because its state changed. Refresh and try again.", "warning", "Transfers.RetryChanged", false)
        };

        /// <summary>Carries the outcome and optional count to the destination locale.</summary>
        private void SetEnrichmentAlert((string Message, string Type, string Key, bool Count) feedback, int count)
        {
            SetAlert(feedback.Message, feedback.Type, feedback.Key);
            if (feedback.Count) TempData["WebAlertCount"] = count;
        }

        /// <summary>Executes a control under the current user's workflow authority.</summary>
        private async Task<IActionResult> ExecuteEnrichmentAsync(
            Func<IImportEnrichmentHandoff, string, Task<EnrichmentCommandResult>> execute)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            if (_enrichmentHandoff is null) return EnrichmentConflict("control-unavailable");
            var result = await execute(_enrichmentHandoff, userId);
            return result.Succeeded ? RedirectToAction(nameof(Index)) : EnrichmentConflict(result.Code);
        }

        /// <summary>Renders a browser explanation while preserving the 409 and existing code response.</summary>
        private IActionResult EnrichmentConflict(string code)
        {
            if (Request.GetTypedHeaders().Accept?.Any(value => value.Quality != 0 &&
                    value.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase)) != true)
                return Conflict(code);

            ViewData["TransferErrorKey"] = LocationTransferText.EnrichmentConflictKey(code);
            var view = View("~/Views/Shared/Error.cshtml", new ErrorViewModel { RequestId = HttpContext.TraceIdentifier });
            view.StatusCode = StatusCodes.Status409Conflict;
            return view;
        }

        /// <summary>Starts or resumes the existing import using its persisted checkpoint.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartImport(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            var result = await _importLifecycle.StartAsync(userId, id, HttpContext.RequestAborted);
            var (message, key) = result.Code switch
            {
                LocationImportCommandCode.Accepted => ("Import started successfully.", "Transfers.ImportStarted"),
                LocationImportCommandCode.ProjectionPending => ("Import accepted and awaiting scheduler recovery.", "Transfers.StartPending"),
                LocationImportCommandCode.InvalidState => ("Import cannot be started in its current state.", "Transfers.StartInvalidState"),
                _ => ("Import record not found.", "Transfers.ImportNotFound")
            };
            SetAlert(message, result.Succeeded ? "success" : "warning", key);
            return RedirectToAction("Index");
        }

        /// <summary>Requests a stop through the existing import lifecycle.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StopImport(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            var result = await _importLifecycle.StopAsync(userId, id, HttpContext.RequestAborted);
            SetAlert(result.Succeeded ? "Import stopping request submitted successfully." : "No active import was found.",
                result.Succeeded ? "success" : "warning",
                result.Succeeded ? "Transfers.StopRequested" : "Transfers.NoActiveImport");
            return RedirectToAction("Index");
        }

        /// <summary>Requests deletion and distinguishes completed cleanup from pending reconciliation.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId)) return Challenge();
            var result = await _importLifecycle.DeleteAsync(userId, id, HttpContext.RequestAborted);
            var (message, key) = result.Code switch
            {
                LocationImportCommandCode.Accepted => ("Upload record removed successfully.", "Transfers.ImportRemoved"),
                LocationImportCommandCode.ProjectionPending =>
                    ("Deletion requested. Upload cleanup is pending reconciliation.", "Transfers.DeletePending"),
                LocationImportCommandCode.ExecutionActive =>
                    ("Upload is active or stopping and cannot be removed yet.", "Transfers.DeleteActive"),
                LocationImportCommandCode.NotFound => ("Upload record not found.", "Transfers.ImportNotFound"),
                _ => ("Upload record could not be removed.", "Transfers.DeleteFailed")
            };
            SetAlert(message, result.Code == LocationImportCommandCode.Accepted ? "success" : "warning", key);
            return RedirectToAction("Index");
        }

        /// <summary>Shows the effective policy and supported upload formats.</summary>
        [HttpGet]
        public IActionResult Upload()
        {
            PrepareUploadView();
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
                    Text = _text["Transfers.FormatOption", _text[$"Transfers.Format.{fileType}"],
                        string.Join(", ", fileType.GetAllowedExtensions())]
                })
                .ToList();

            fileTypes.Insert(0, new SelectListItem { Value = "", Text = _text["Transfers.SelectFormat"] });
            ViewBag.FileTypes = fileTypes;

            var acceptedExtensions = Enum.GetValues(typeof(LocationImportFileType))
                .Cast<LocationImportFileType>()
                .Where(fileType => fileType.IsSupportedUpload())
                .SelectMany(fileType => fileType.GetAllowedExtensions())
                .Distinct(StringComparer.OrdinalIgnoreCase);
            ViewBag.AcceptedExtensions = string.Join(",", acceptedExtensions);

            var uploadSettings = _dbContext.ApplicationSettings.OrderBy(s => s.Id).FirstOrDefault();
            // Display the same effective policy used before multipart buffering.
            var effectiveLimit = Wayfarer.Util.UploadRequestPolicy.EffectiveMiB(
                uploadSettings?.UploadSizeLimitMB ?? ApplicationSettings.DefaultUploadSizeLimitMB);
            ViewBag.UploadLimit = effectiveLimit;
            ViewBag.UploadsDisabled = effectiveLimit < 0;
        }

        /// <summary>Preserves the submitted model and required view data after validation failure.</summary>
        private ViewResult InvalidUpload(LocationImportUploadViewModel model)
        {
            PrepareUploadView();
            return View("Upload", model);
        }

        /// <summary>Validates the chosen input and stages an import under the current owner.</summary>
        [HttpPost]
        [ValidateAntiForgeryToken]
        [Wayfarer.Middleware.UserFileUpload(LocalizeRequestRefusal = true)]
        public async Task<IActionResult> Upload(LocationImportUploadViewModel model)
        {
            if (!ModelState.IsValid)
            {
                return InvalidUpload(model);
            }

            if (model.File == null || model.File.Length == 0)
            {
                ModelState.AddModelError(nameof(model.File), _text["Transfers.InvalidFile"]);
                return InvalidUpload(model);
            }

            if (!model.FileType.HasValue)
            {
                ModelState.AddModelError(nameof(model.FileType), _text["Transfers.InvalidFormat"]);
                return InvalidUpload(model);
            }

            if (!model.FileType.Value.IsSupportedUpload())
            {
                ModelState.AddModelError(nameof(model.FileType),
                    _text["Transfers.UnsupportedGeoJson"]);
                return InvalidUpload(model);
            }

            var extension = Path.GetExtension(model.File.FileName);
            if (!model.FileType.Value.IsExtensionValid(extension))
            {
                ModelState.AddModelError(nameof(model.File),
                    _text["Transfers.InvalidExtension", extension, string.Join(", ", model.FileType.Value.GetAllowedExtensions())]);
                return InvalidUpload(model);
            }

            if (!ValidateModelState())
            {
                return InvalidUpload(model);
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                SetAlert("User not authenticated.", "danger", "Transfers.SignInRequired");
                return RedirectToAction("Index", "Home", new { area = "" });
            }

            var reference = LocationImportStagedFiles.CreateReference(model.FileType.Value);
            string? filePath = null;
            var stagedFileCreated = false;
            var committed = false;
            LocationImport importRecord;

            try
            {
                if (!_stagedFiles.TryResolve(reference, out filePath))
                    throw new InvalidOperationException("Invalid generated import reference.");
                // Only the owned staging directory is created; installation roots remain administrator-owned.
                Directory.CreateDirectory(_stagedFiles.DirectoryPath);
                using (var stream = new FileStream(filePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stagedFileCreated = true;
                    await model.File.CopyToAsync(stream);
                }

                importRecord = new LocationImport
                {
                    UserId = userId,
                    FileType = model.FileType.Value,
                    FilePath = reference,
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
                committed = true;
            }
            catch (Exception) when (!committed)
            {
                var cleanupFailed = false;
                if (stagedFileCreated && filePath is not null)
                {
                    try { if (System.IO.File.Exists(filePath)) System.IO.File.Delete(filePath); }
                    catch (Exception)
                    {
                        cleanupFailed = true;
                    }
                }

                try
                {
                    _logger.LogError("Location import upload failed; code {Code}.",
                        "location-import-upload-failed");
                }
                catch (Exception)
                {
                    // Diagnostics are best effort after request-local consistency restoration.
                }

                if (cleanupFailed)
                {
                    try
                    {
                        _logger.LogWarning("Location import upload cleanup failed; code {Code}.",
                            "location-import-upload-cleanup-failed");
                    }
                    catch (Exception)
                    {
                        // A cleanup diagnostic cannot replace the primary upload failure.
                    }
                }

                TrySetUploadFailureAlert();

                return RedirectToAction("Index");
            }

            // Relational persistence is authoritative; presentation diagnostics are best effort.
            try
            {
                _logger.LogInformation("Location import upload staged; code {Code}; import {ImportId}.",
                    "location-import-upload-staged", importRecord.Id);
            }
            catch (Exception)
            {
                // A diagnostic sink cannot invalidate the committed row and staged file.
            }

            try
            {
                SetAlert("File uploaded successfully and is pending import.", "success", "Transfers.Uploaded");
            }
            catch (Exception)
            {
                // The durable import list remains authoritative when banner storage fails.
            }

            return RedirectToAction("Index");
        }

        /// <summary>Attempts bounded upload-failure diagnostics and presentation independently.</summary>
        private void TrySetUploadFailureAlert()
        {
            const string message = "An unexpected error occurred. Please try again later.";
            const string alertType = "danger";

            try
            {
                _logger.LogInformation("Alert: {Message} ({Type})", message, alertType);
            }
            catch (Exception)
            {
                // Alert diagnostics cannot suppress the available presentation sink.
            }

            try
            {
                TempData["AlertMessage"] = message;
                TempData["WebAlertKey"] = "Transfers.UploadFailed";
            }
            catch (Exception)
            {
                // Presentation storage cannot replace cleanup or bounded redirect behavior.
            }

            try
            {
                TempData["AlertType"] = alertType;
            }
            catch (Exception)
            {
                // Each presentation value remains independently best effort.
            }
        }
    }
}
