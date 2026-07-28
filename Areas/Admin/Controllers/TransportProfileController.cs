using System.Security.Claims;
using System.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Text.RegularExpressions;
using Wayfarer.Areas.Admin.Models;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.Areas.Admin.Controllers;

/// <summary>Admin-only management surface for the database transport-profile catalog.</summary>
[Area("Admin")]
[Authorize(Roles = "Admin")]
public sealed class TransportProfileController : Controller
{
    private const int PageSize = 15;
    private readonly ApplicationDbContext _dbContext;

    /// <summary>Initializes the controller.</summary>
    public TransportProfileController(ApplicationDbContext dbContext) => _dbContext = dbContext;

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
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key)), profile.RowVersion))
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
            ModelState.AddModelError(nameof(model.Key), "Use lowercase letters, numbers, and single hyphens only.");
        }
        if (string.IsNullOrWhiteSpace(model.Label))
        {
            ModelState.AddModelError(nameof(model.Label), "Label is required.");
        }
        if (string.IsNullOrWhiteSpace(model.Category))
        {
            ModelState.AddModelError(nameof(model.Category), "Category is required.");
        }
        if (await _dbContext.Set<TransportProfile>().AnyAsync(profile => profile.Key == model.Key, cancellationToken))
        {
            ModelState.AddModelError(nameof(model.Key), "That transport-profile key already exists.");
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
        catch (DbUpdateException)
        {
            _dbContext.Entry(profile).State = EntityState.Detached;
            _dbContext.Entry(audit).State = EntityState.Detached;
            ModelState.AddModelError(nameof(model.Key), "That transport-profile key could not be created because it conflicts with current catalog state.");
            return View(model);
        }
        TempData["AlertMessage"] = "Transport profile created.";
        TempData["AlertType"] = "success";
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

        await using var transaction = await _dbContext.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken);

        var profile = await _dbContext.Set<TransportProfile>().SingleOrDefaultAsync(item => item.Id == id, cancellationToken);
        if (profile == null)
        {
            return NotFound();
        }

        model.Key = profile.Key;
        model.ReferencedSegments = await new TransportProfileCatalog(_dbContext).CountReferencesAsync(id, profile.Key, cancellationToken);
        var speedChanged = profile.PlanningSpeedKmh != model.PlanningSpeedKmh;
        if (speedChanged && model.ReferencedSegments > 0)
        {
            ModelState.AddModelError(nameof(model.PlanningSpeedKmh), "Referenced planning-speed changes require #405 duration provenance and reconciliation.");
        }
        if (profile.IsActive && !model.IsActive && !model.ConfirmDeactivation)
        {
            ModelState.AddModelError(nameof(model.ConfirmDeactivation), "Confirm deactivation after reviewing the dependency count.");
        }
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
            ModelState.AddModelError(string.Empty, "This profile changed after the form was loaded. Review the current values and try again.");
            var current = await BuildEditModelAsync(id, cancellationToken);
            return current == null ? NotFound() : View(current);
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            _dbContext.Entry(profile).State = EntityState.Detached;
            _dbContext.Entry(audit).State = EntityState.Detached;
            ModelState.AddModelError(string.Empty, "The profile or its dependencies changed concurrently. No changes were saved.");
            var current = await BuildEditModelAsync(id, cancellationToken);
            return current == null ? NotFound() : View(current);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            _dbContext.Entry(profile).State = EntityState.Detached;
            _dbContext.Entry(audit).State = EntityState.Detached;
            ModelState.AddModelError(string.Empty, "The profile or its dependencies changed concurrently. No changes were saved.");
            var current = await BuildEditModelAsync(id, cancellationToken);
            return current == null ? NotFound() : View(current);
        }

        TempData["AlertMessage"] = "Transport profile updated.";
        TempData["AlertType"] = "success";
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
            ModelState.AddModelError(string.Empty, profile.IsSeeded ? "Seeded profiles cannot be deleted; deactivate them instead." : "Referenced profiles cannot be deleted; deactivate them instead.");
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
            ModelState.AddModelError(string.Empty, "This profile changed after the confirmation was loaded.");
            return View("Delete", await BuildRowAsync(id, cancellationToken));
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            _dbContext.Entry(profile).State = EntityState.Detached;
            _dbContext.Entry(audit).State = EntityState.Detached;
            ModelState.AddModelError(string.Empty, "The profile gained a dependency and cannot be deleted. Deactivate it instead.");
            return View("Delete", await BuildRowAsync(id, cancellationToken));
        }
        catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.SerializationFailure)
        {
            await transaction.RollbackAsync(cancellationToken);
            _dbContext.Entry(profile).State = EntityState.Detached;
            _dbContext.Entry(audit).State = EntityState.Detached;
            ModelState.AddModelError(string.Empty, "The profile or its dependencies changed concurrently. It was not deleted.");
            return View("Delete", await BuildRowAsync(id, cancellationToken));
        }

        TempData["AlertMessage"] = "Transport profile deleted.";
        TempData["AlertType"] = "success";
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
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key))
            }).SingleOrDefaultAsync(token);

    private Task<TransportProfileRowViewModel?> BuildRowAsync(Guid id, CancellationToken token) =>
        _dbContext.Set<TransportProfile>().AsNoTracking().Where(profile => profile.Id == id)
            .Select(profile => new TransportProfileRowViewModel(profile.Id, profile.Key, profile.Label, profile.Category,
                profile.PlanningSpeedKmh, profile.SortOrder, profile.IsActive, profile.IsSeeded,
                _dbContext.Segments.Count(segment => segment.TransportProfileId == profile.Id
                    || (segment.TransportProfileId == null && segment.Mode.Trim().ToLower() == profile.Key)), profile.RowVersion))
            .SingleOrDefaultAsync(token);

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
}
