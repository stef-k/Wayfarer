using Microsoft.EntityFrameworkCore;
using System.Xml.Linq;
using Wayfarer.Models;
using Wayfarer.Parsers;

namespace Wayfarer.Services;

public partial class TripImportService
{
    /// <summary>Budgets detached generic geometry before tracking and persists it atomically.</summary>
    private async Task<TripImportResult> ImportGenericAsync(
        XDocument source,
        string userId,
        TripImportMode mode,
        CancellationToken cancellationToken)
    {
        if (mode == TripImportMode.Upsert)
            throw new InvalidOperationException("Generic KML upsert is not supported.");
        var parsed = GoogleMyMapsKmlParser.Parse(source, userId, cancellationToken);
        var target = CreateNewShell(parsed.Trip, userId);
        target.Name = $"{target.Name} (Imported)";
        var importedTagTokens = target.Tags.Select(tag => tag.Slug).ToArray();
        target.Tags.Clear();

        await using var transaction = _dbContext.Database.IsRelational()
            ? await _dbContext.Database.BeginTransactionAsync(cancellationToken)
            : null;
        try
        {
            var reconciledTags = await _tagReconciler.ReconcileAsync(importedTagTokens, cancellationToken);
            foreach (var tag in reconciledTags) target.Tags.Add(tag);
            AddShadowRegion(target, userId);
            _dbContext.Trips.Add(target);
            await _dbContext.SaveChangesAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            await SegmentMeasurementWriterReconciler.ReconcileTripAsync(
                _dbContext, target.Id, allowUnavailableAutomatic: true, cancellationToken);
            if (transaction is not null) await transaction.CommitAsync(cancellationToken);
            _dbContext.ChangeTracker.Clear();
            return new(target.Id, parsed.Notices, target.Segments.Count > 0);
        }
        catch
        {
            if (transaction is not null) await transaction.RollbackAsync(CancellationToken.None);
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    /// <summary>Adds the required generic import shadow region without changing imported region semantics.</summary>
    private static void AddShadowRegion(Trip target, string userId)
    {
        const string shadowName = "Unassigned Places";
        if (target.Regions.Any(region => region.Name == shadowName)) return;
        foreach (var region in target.Regions) region.DisplayOrder++;
        target.Regions.Add(new()
        {
            Id = Guid.NewGuid(),
            TripId = target.Id,
            UserId = userId,
            Name = shadowName,
            DisplayOrder = 0,
            Places = []
        });
    }
}
