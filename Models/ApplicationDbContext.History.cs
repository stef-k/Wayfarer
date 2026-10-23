using Microsoft.EntityFrameworkCore;
using Wayfarer.Util;

namespace Wayfarer.Models;

/// <summary>Owns atomic final-row derivation for every tracked Location writer, including sync callers.</summary>
public partial class ApplicationDbContext
{
    /// <summary>Durable independent preparation generations and progress.</summary>
    public DbSet<HistoryPreparationState> HistoryPreparationStates { get; set; }

    /// <summary>All async save entry points share the same atomic boundary.</summary>
    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        SaveHistoryChangesAsync(() => SaveApplicationChangesAsync(true, cancellationToken), cancellationToken);

    /// <summary>Synchronous callers retain the same derivation contract.</summary>
    public override int SaveChanges() => SaveChanges(true);

    /// <summary>Synchronous callers retain explicit change-acceptance semantics.</summary>
    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveHistoryChangesAsync(() => Task.FromResult(base.SaveChanges(acceptAllChangesOnSuccess)), CancellationToken.None)
            .GetAwaiter().GetResult();

    /// <summary>Locks current rows, overlays only modified canonical properties and saves derived values together.</summary>
    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default) =>
        SaveHistoryChangesAsync(() => base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken), cancellationToken);

    /// <summary>Preserves each existing save overload's non-Location behavior while adding the same history boundary.</summary>
    private async Task<int> SaveHistoryChangesAsync(Func<Task<int>> save, CancellationToken cancellationToken)
    {
        var entries = ChangeTracker.Entries<Location>()
            .Where(entry => entry.State is EntityState.Added or EntityState.Modified).OrderBy(entry => entry.Entity.Id).ToArray();
        await using var transaction = entries.Length > 0 && Database.IsRelational() && Database.CurrentTransaction == null
            ? await Database.BeginTransactionAsync(cancellationToken) : null;
        foreach (var entry in entries)
        {
            var added = entry.State == EntityState.Added;
            var modified = entry.Properties.Where(property => property.IsModified && !LocationHistoryDerivation.IsDerived(property.Metadata.Name))
                .Select(property => property.Metadata.Name).ToArray();
            Location final = entry.Entity;
            if (!added)
            {
                // An untracked reload avoids EF identity resolution returning a stale partial draft.
                final = Database.IsNpgsql()
                    ? await Locations.FromSqlInterpolated($$"""
                        SELECT * FROM "Locations" WHERE "Id" = {{entry.Entity.Id}} FOR UPDATE
                        """).AsNoTracking().SingleOrDefaultAsync(cancellationToken)
                        ?? throw new DbUpdateConcurrencyException("Location disappeared before save.")
                    : await Locations.AsNoTracking().SingleOrDefaultAsync(row => row.Id == entry.Entity.Id, cancellationToken)
                        ?? throw new DbUpdateConcurrencyException("Location disappeared before save.");
                foreach (var name in modified) Entry(final).Property(name).CurrentValue = entry.Property(name).CurrentValue;
            }
            var time = added || final.HistoryTimeVersion != LocationHistoryDerivation.TimeVersion
                || modified.Any(LocationHistoryDerivation.IsTimeInput);
            var text = added || final.HistoryTextVersion != LocationHistoryDerivation.TextVersion
                || modified.Any(LocationHistoryDerivation.IsTextInput);
            LocationHistoryDerivation.Apply(final, time, text);
            foreach (var property in entry.Properties.Where(property => LocationHistoryDerivation.IsDerived(property.Metadata.Name)))
            {
                property.CurrentValue = Entry(final).Property(property.Metadata.Name).CurrentValue;
                // Ignore client/attached derived assignments; write only the generations we recomputed.
                if (!added) property.IsModified = property.Metadata.Name is nameof(Location.HistorySearchText) or nameof(Location.HistoryTextVersion) ? text : time;
            }
        }
        var count = await save();
        if (transaction != null) await transaction.CommitAsync(cancellationToken);
        return count;
    }
}
