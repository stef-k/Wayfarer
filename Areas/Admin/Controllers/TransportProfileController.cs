using System.Security.Claims;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using System.Text.RegularExpressions;
using Wayfarer.Areas.Admin.Models;
using Wayfarer.Models;
using Wayfarer.Localization;
using Wayfarer.Services;

namespace Wayfarer.Areas.Admin.Controllers;

/// <summary>Admin-only management surface for the database transport-profile catalog.</summary>
[Area("Admin")]
[Authorize(Roles = "Admin")]
public sealed class TransportProfileController : Controller
{
    private const int PageSize = 15;
    private const string UniqueKeyConstraint = "IX_TransportProfiles_Key";
    private const string SegmentTransportProfileConstraint = "FK_Segments_TransportProfiles_TransportProfileId";
    private readonly ApplicationDbContext _dbContext;
    private readonly ILogger<TransportProfileController> _logger;
    private readonly WebMessages _text;

    /// <summary>Initializes the controller.</summary>
    public TransportProfileController(ApplicationDbContext dbContext, ILogger<TransportProfileController> logger, WebMessages text)
    {
        _dbContext = dbContext;
        _logger = logger;
        _text = text;
    }

    /// <summary>Lists profiles with deterministic search, ordering, pagination, and dependencies.</summary>
    public async Task<IActionResult> Index(string search = "", int page = 1, CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        search = search?.Trim() ?? string.Empty;
        var query = _dbContext.Set<TransportProfile>().AsNoTracking()
            .Where(profile => search == string.Empty || profile.Key.Contains(search) || profile.Label.Contains(search) || profile.Category.Contains(search));
        var total = await query.CountAsync(cancellationToken);
        var rows = await query
            .OrderBy(profile => profile.SortOrder).ThenBy(profile => profile.Label).ThenBy(profile => profile.Key)
            .Skip((page - 1) * PageSize).Take(PageSize)
            .Select(profile => new TransportProfileRowViewModel(
                profile.Id, profile.Key, profile.Label, profile.Category, profile.PlanningSpeedKmh,
                profile.SortOrder, profile.IsActive, profile.IsSeeded,
                _dbContext.Segments.Count(segment => segment.TransportProfileId == profile.Id
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key)),
                _dbContext.Segments.Count(segment => (segment.TransportProfileId == profile.Id
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key))
                    && segment.EstimatedDurationSource == EstimatedDurationSource.Automatic),
                _dbContext.Segments.Count(segment => (segment.TransportProfileId == profile.Id
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key))
                    && segment.EstimatedDurationSource == EstimatedDurationSource.Manual), profile.RowVersion))
            .ToListAsync(cancellationToken);
        return View(new TransportProfileIndexViewModel(rows, search, page, Math.Max(1, (int)Math.Ceiling(total / (double)PageSize))));
    }

    /// <summary>Displays the allowlisted create form.</summary>
    public IActionResult Create() => View(new TransportProfileCreateViewModel());

    /// <summary>Creates a normalized unique profile and a bounded audit record atomically.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(TransportProfileCreateViewModel model, CancellationToken cancellationToken)
    {
        model.Key = TransportProfile.NormalizeKey(model.Key ?? string.Empty);
        ModelState.Remove(nameof(model.Key));
        if (!Regex.IsMatch(model.Key, "^[a-z0-9]+(?:-[a-z0-9]+)*$"))
        {
            ModelState.AddModelError(nameof(model.Key), _text["Catalogs.Validation.KeySyntax"]);
        }
        if (string.IsNullOrWhiteSpace(model.Label))
        {
            ModelState.AddModelError(nameof(model.Label), _text["Catalogs.Validation.LabelRequired"]);
        }
        if (string.IsNullOrWhiteSpace(model.Category))
        {
            ModelState.AddModelError(nameof(model.Category), _text["Catalogs.Validation.CategoryRequired"]);
        }
        if (await _dbContext.Set<TransportProfile>().AnyAsync(profile => profile.Key == model.Key, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.Key), _text["Catalogs.Validation.KeyDuplicate"]);
        }

        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var profile = new TransportProfile
        {
            Id = Guid.NewGuid(), Key = model.Key, Label = model.Label.Trim(), Category = model.Category.Trim(),
            PlanningSpeedKmh = model.PlanningSpeedKmh, SortOrder = model.SortOrder, IsActive = model.IsActive,
            Description = NormalizeDescription(model.Description), IsSeeded = false
        };
        _dbContext.Set<TransportProfile>().Add(profile);
        var audit = AddAudit("TransportProfileCreate", profile, "created", 0);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsDuplicateKey(exception))
        {
            DetachFailedCreate(profile, audit);
            ModelState.AddModelError(nameof(model.Key), _text["Catalogs.Validation.KeyConflict"]);
            return View(model);
        }
        catch (DbUpdateException)
        {
            DetachFailedCreate(profile, audit);
            _logger.LogError("Transport profile creation failed without persisting changes. ProfileId={ProfileId}", profile.Id);
            ModelState.AddModelError(string.Empty, _text["Catalogs.TransportCreateFailed"]);
            return View(model);
        }
        WebAlert.Store(TempData, "Transport profile created.", "success", "Catalogs.TransportCreated");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Displays mutable fields, immutable key, dependencies, and concurrency state.</summary>
    public async Task<IActionResult> Edit(Guid id, CancellationToken cancellationToken)
    {
        var model = await BuildEditModelAsync(id, cancellationToken);
        return model == null ? NotFound() : View(model);
    }

    /// <summary>Updates only allowlisted fields while enforcing dependencies and optimistic concurrency.</summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(Guid id, TransportProfileEditViewModel model, CancellationToken cancellationToken)
    {
        if (id != model.Id)
        {
            return NotFound();
        }

        var snapshot = await _dbContext.Set<TransportProfile>().AsNoTracking()
            .SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (snapshot == null) return NotFound();
        var counts = await LoadDependencyCountsAsync(snapshot, cancellationToken);
        model.Key = snapshot.Key;
        model.ReferencedSegments = counts.Total;
        model.AutomaticSegments = counts.Automatic;
        model.ManualSegments = counts.Manual;
        var speedChangedBeforeLock = snapshot.PlanningSpeedKmh != model.PlanningSpeedKmh;
        if (speedChangedBeforeLock && counts.Total > 0 && !model.ConfirmReferencedSpeedChange)
            ModelState.AddModelError(nameof(model.ConfirmReferencedSpeedChange), _text["Catalogs.Validation.ConfirmSpeed"]);
        if (snapshot.IsActive && !model.IsActive && !model.ConfirmDeactivation)
            ModelState.AddModelError(nameof(model.ConfirmDeactivation), _text["Catalogs.Validation.ConfirmDeactivation"]);
        if (!ModelState.IsValid) return View(model);

        if (speedChangedBeforeLock)
        {
            var update = new TransportProfileUpdateProposal(
                model.Label.Trim(), model.Category.Trim(), model.PlanningSpeedKmh, model.SortOrder,
                model.IsActive, NormalizeDescription(model.Description), model.RowVersion);
            TransportProfileMeasurementResult result;
            try
            {
                result = await TransportProfileMeasurementReconciler.ReconcileUpdateAsync(
                    _dbContext, id, update, User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "admin", cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                _logger.LogWarning("Referenced transport-profile reconciliation encountered optimistic concurrency.");
                ModelState.AddModelError(string.Empty, _text["Catalogs.TransportStale"]);
                return View(model);
            }
            catch (Exception exception) when (IsSerializationFailure(exception))
            {
                _logger.LogWarning("Referenced transport-profile reconciliation encountered serialization concurrency.");
                ModelState.AddModelError(string.Empty, _text["Catalogs.TransportConflict"]);
                return View(model);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Referenced transport-profile reconciliation was cancelled.");
                ModelState.AddModelError(string.Empty, _text["Catalogs.TransportUpdateCancelled"]);
                return View(model);
            }
            catch (DbUpdateException)
            {
                _logger.LogError("Referenced transport-profile reconciliation failed provider integrity checks.");
                ModelState.AddModelError(string.Empty, _text["Catalogs.TransportIntegrity"]);
                return View(model);
            }
            catch (Exception)
            {
                _logger.LogError("Referenced transport-profile reconciliation failed unexpectedly without persisting changes.");
                ModelState.AddModelError(string.Empty, _text["Catalogs.TransportSaveFailed"]);
                return View(model);
            }
            if (!result.Succeeded)
            {
                ModelState.AddModelError(string.Empty, TransportProfileWebText.ReconciliationError(_text, result.Errors[0]));
                return View(model);
            }
            WebAlert.Store(TempData, "Transport profile updated.", "success", "Catalogs.TransportReconciled",
                numbers: [result.RecalculatedReferences, result.UnavailableReferences]);
            return RedirectToAction(nameof(Index));
        }

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var profile = await _dbContext.Set<TransportProfile>().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (profile == null)
        {
            return NotFound();
        }

        model.Key = profile.Key;
        model.ReferencedSegments = await new TransportProfileCatalog(_dbContext).CountReferencesAsync(id, profile.Key, cancellationToken);
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var changed = ChangedFields(profile, model);
        profile.Label = model.Label.Trim();
        profile.Category = model.Category.Trim();
        profile.PlanningSpeedKmh = model.PlanningSpeedKmh;
        profile.SortOrder = model.SortOrder;
        profile.IsActive = model.IsActive;
        profile.Description = NormalizeDescription(model.Description);
        _dbContext.Entry(profile).Property(item => item.RowVersion).OriginalValue = model.RowVersion;
        var audit = AddAudit("TransportProfileUpdate", profile, changed, model.ReferencedSegments);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            _dbContext.Entry(profile).State = EntityState.Detached;
            _dbContext.Entry(audit).State = EntityState.Detached;
            ModelState.AddModelError(string.Empty, _text["Catalogs.TransportStale"]);
            var current = await BuildEditModelAsync(id, cancellationToken);
            return current == null ? NotFound() : View(current);
        }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            DetachFailedMutation(profile, audit);
            ModelState.AddModelError(string.Empty, _text["Catalogs.TransportConflict"]);
            var current = await BuildEditModelAsync(id, cancellationToken);
            return current == null ? NotFound() : View(current);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            DetachFailedMutation(profile, audit);
            _logger.LogError("Transport profile update failed without persisting changes.");
            ModelState.AddModelError(string.Empty, _text["Catalogs.TransportSaveFailed"]);
            return View(model);
        }

        WebAlert.Store(TempData, "Transport profile updated.", "success", "Catalogs.TransportUpdated");
        return RedirectToAction(nameof(Index));
    }

    /// <summary>Displays deletion dependencies and immutable identity.</summary>
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var row = await BuildRowAsync(id, cancellationToken);
        return row == null ? NotFound() : View(row);
    }

    /// <summary>Deletes only an unreferenced non-seeded profile after antiforgery and concurrency checks.</summary>
    [HttpPost, ActionName("Delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteConfirmed(Guid id, uint rowVersion, CancellationToken cancellationToken)
    {
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);
        var profile = await _dbContext.Set<TransportProfile>().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (profile == null)
        {
            return NotFound();
        }
        var referenced = await new TransportProfileCatalog(_dbContext).CountReferencesAsync(id, profile.Key, cancellationToken);
        if (profile.IsSeeded || referenced > 0)
        {
            ModelState.AddModelError(string.Empty, _text[profile.IsSeeded ? "Catalogs.TransportSeededRefusal" : "Catalogs.TransportReferencedRefusal"]);
            return View("Delete", await BuildRowAsync(id, cancellationToken));
        }
        _dbContext.Entry(profile).Property(item => item.RowVersion).OriginalValue = rowVersion;
        _dbContext.Set<TransportProfile>().Remove(profile);
        var audit = AddAudit("TransportProfileDelete", profile, "deleted", 0);
        try
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            await transaction.RollbackAsync(cancellationToken);
            _dbContext.Entry(profile).State = EntityState.Detached;
            _dbContext.Entry(audit).State = EntityState.Detached;
            ModelState.AddModelError(string.Empty, _text["Catalogs.TransportDeleteStale"]);
            return View("Delete", await BuildRowAsync(id, cancellationToken));
        }
        catch (DbUpdateException exception) when (IsSegmentDependencyViolation(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            DetachFailedMutation(profile, audit);
            ModelState.AddModelError(string.Empty, _text["Catalogs.TransportDependencyRace"]);
            return View("Delete", await BuildRowAsync(id, cancellationToken));
        }
        catch (Exception exception) when (IsSerializationFailure(exception))
        {
            await transaction.RollbackAsync(cancellationToken);
            DetachFailedMutation(profile, audit);
            ModelState.AddModelError(string.Empty, _text["Catalogs.TransportDeleteConflict"]);
            return View("Delete", await BuildRowAsync(id, cancellationToken));
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            DetachFailedMutation(profile, audit);
            _logger.LogError("Transport profile deletion failed without persisting changes.");
            ModelState.AddModelError(string.Empty, _text["Catalogs.TransportDeleteFailed"]);
            return View("Delete", await BuildRowAsync(id, cancellationToken));
        }

        WebAlert.Store(TempData, "Transport profile deleted.", "success", "Catalogs.TransportDeleted");
        return RedirectToAction(nameof(Index));
    }

    private Task<TransportProfileEditViewModel?> BuildEditModelAsync(Guid id, CancellationToken token) =>
        _dbContext.Set<TransportProfile>().AsNoTracking().Where(profile => profile.Id == id)
            .Select(profile => new TransportProfileEditViewModel
            {
                Id = profile.Id, Key = profile.Key, Label = profile.Label, Category = profile.Category,
                PlanningSpeedKmh = profile.PlanningSpeedKmh, SortOrder = profile.SortOrder, IsActive = profile.IsActive,
                Description = profile.Description, RowVersion = profile.RowVersion, WasActive = profile.IsActive,
                ReferencedSegments = _dbContext.Segments.Count(segment => segment.TransportProfileId == profile.Id
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key)),
                AutomaticSegments = _dbContext.Segments.Count(segment => (segment.TransportProfileId == profile.Id
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key))
                    && segment.EstimatedDurationSource == EstimatedDurationSource.Automatic),
                ManualSegments = _dbContext.Segments.Count(segment => (segment.TransportProfileId == profile.Id
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key))
                    && segment.EstimatedDurationSource == EstimatedDurationSource.Manual)
            }).SingleOrDefaultAsync(token);

    private Task<TransportProfileRowViewModel?> BuildRowAsync(Guid id, CancellationToken token) =>
        _dbContext.Set<TransportProfile>().AsNoTracking().Where(profile => profile.Id == id)
            .Select(profile => new TransportProfileRowViewModel(profile.Id, profile.Key, profile.Label, profile.Category,
                profile.PlanningSpeedKmh, profile.SortOrder, profile.IsActive, profile.IsSeeded,
                _dbContext.Segments.Count(segment => segment.TransportProfileId == profile.Id
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key)),
                _dbContext.Segments.Count(segment => (segment.TransportProfileId == profile.Id
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key))
                    && segment.EstimatedDurationSource == EstimatedDurationSource.Automatic),
                _dbContext.Segments.Count(segment => (segment.TransportProfileId == profile.Id
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key))
                    && segment.EstimatedDurationSource == EstimatedDurationSource.Manual), profile.RowVersion))
            .SingleOrDefaultAsync(token);

    private async Task<(int Total, int Automatic, int Manual)> LoadDependencyCountsAsync(
        TransportProfile profile,
        CancellationToken cancellationToken)
    {
        var sources = await _dbContext.Segments.AsNoTracking()
            .Where(segment => segment.TransportProfileId == profile.Id
                || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key))
            .Select(segment => segment.EstimatedDurationSource)
            .ToArrayAsync(cancellationToken);
        return (sources.Length, sources.Count(source => source == EstimatedDurationSource.Automatic),
            sources.Count(source => source == EstimatedDurationSource.Manual));
    }

    private AuditLog AddAudit(string action, TransportProfile profile, string changedFields, int dependencies)
    {
        var audit = new AuditLog
        {
            UserId = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "admin",
            Action = action,
            Timestamp = DateTime.UtcNow,
            Details = $"ProfileId={profile.Id};Fields={changedFields};ReferencedSegments={dependencies}"
        };
        _dbContext.AuditLogs.Add(audit);
        return audit;
    }

    private static string ChangedFields(TransportProfile profile, TransportProfileEditViewModel model)
    {
        var fields = new List<string>();
        if (profile.Label != model.Label.Trim()) fields.Add("Label");
        if (profile.Category != model.Category.Trim()) fields.Add("Category");
        if (profile.PlanningSpeedKmh != model.PlanningSpeedKmh) fields.Add("PlanningSpeedKmh");
        if (profile.SortOrder != model.SortOrder) fields.Add("SortOrder");
        if (profile.IsActive != model.IsActive) fields.Add("IsActive");
        if (profile.Description != NormalizeDescription(model.Description)) fields.Add("Description");
        return fields.Count == 0 ? "none" : string.Join(',', fields);
    }

    private static string? NormalizeDescription(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool IsDuplicateKey(DbUpdateException exception) =>
        FindPostgresException(exception) is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.UniqueViolation
        && postgres.ConstraintName == UniqueKeyConstraint;

    /// <summary>Identifies the precise PostgreSQL serialization SQLSTATE through provider wrappers.</summary>
    private static bool IsSerializationFailure(Exception exception) =>
        FindPostgresException(exception)?.SqlState == PostgresErrorCodes.SerializationFailure;

    /// <summary>Identifies only the restrictive segment-to-profile foreign-key race.</summary>
    private static bool IsSegmentDependencyViolation(DbUpdateException exception) =>
        FindPostgresException(exception) is PostgresException postgres
        && postgres.SqlState == PostgresErrorCodes.ForeignKeyViolation
        && postgres.ConstraintName == SegmentTransportProfileConstraint;

    /// <summary>Finds a PostgreSQL provider exception without matching localized messages.</summary>
    private static PostgresException? FindPostgresException(Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException!)
        {
            if (current is PostgresException postgres)
            {
                return postgres;
            }
        }

        return null;
    }

    private void DetachFailedCreate(TransportProfile profile, AuditLog audit)
    {
        _dbContext.Entry(profile).State = EntityState.Detached;
        _dbContext.Entry(audit).State = EntityState.Detached;
    }

    /// <summary>Clears failed profile and audit mutations before rebuilding a safe response.</summary>
    private void DetachFailedMutation(TransportProfile profile, AuditLog audit) => DetachFailedCreate(profile, audit);
}
