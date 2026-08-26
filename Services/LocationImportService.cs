using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationImports;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Parsers;

public interface ILocationImportService
{
    Task ProcessImport(int importId, CancellationToken cancellationToken);
}

/// <summary>Exposes a bounded execution outcome to the Quartz job/listener seam.</summary>
public interface ILocationImportExecutionService
{
    Task<LocationImportExecutionOutcome> ProcessImportExecution(int importId, int epoch, CancellationToken cancellationToken);
}

/// <summary>Streams and persists location imports with batch-scoped database ownership.</summary>
public sealed class LocationImportService : ILocationImportService, ILocationImportExecutionService
{
    private const string SafeProgressEvent = """{"type":"import-state"}""";
    private const int BatchSize = 50;
    private readonly IDbContextFactory<ApplicationDbContext> _contexts;
    private readonly ILogger<LocationImportService> _logger;
    private readonly LocationDataParserFactory _parserFactory;
    private readonly SseService _sse;
    private readonly IImportEnrichmentHandoff? _enrichmentHandoff;
    private readonly ILocationImportLifecycleObserver _lifecycleObserver;

    public LocationImportService(IDbContextFactory<ApplicationDbContext> contexts,
        ReverseGeocodingService reverseGeocodingService, ILogger<LocationImportService> logger,
        LocationDataParserFactory parserFactory, SseService sse,
        IImportEnrichmentHandoff? enrichmentHandoff = null)
        : this(contexts, reverseGeocodingService, logger, parserFactory, sse, enrichmentHandoff,
            NullLocationImportLifecycleObserver.Instance)
    { }

    /// <summary>Retains source compatibility for focused tests while production uses a factory.</summary>
    internal LocationImportService(ApplicationDbContext context,
        ReverseGeocodingService reverseGeocodingService, ILogger<LocationImportService> logger,
        LocationDataParserFactory parserFactory, SseService sse,
        IImportEnrichmentHandoff? enrichmentHandoff = null)
        : this(new CloningContextFactory(context), reverseGeocodingService, logger, parserFactory, sse,
            enrichmentHandoff, NullLocationImportLifecycleObserver.Instance)
    { }

    /// <summary>Retains observer-enabled source compatibility for focused tests.</summary>
    internal LocationImportService(ApplicationDbContext context,
        ReverseGeocodingService reverseGeocodingService, ILogger<LocationImportService> logger,
        LocationDataParserFactory parserFactory, SseService sse,
        IImportEnrichmentHandoff? enrichmentHandoff, ILocationImportLifecycleObserver lifecycleObserver)
        : this(new CloningContextFactory(context), reverseGeocodingService, logger, parserFactory, sse,
            enrichmentHandoff, lifecycleObserver)
    { }

    /// <summary>Creates a worker with a test-controlled lifecycle observer.</summary>
    internal LocationImportService(IDbContextFactory<ApplicationDbContext> contexts,
        ReverseGeocodingService reverseGeocodingService, ILogger<LocationImportService> logger,
        LocationDataParserFactory parserFactory, SseService sse,
        IImportEnrichmentHandoff? enrichmentHandoff, ILocationImportLifecycleObserver lifecycleObserver)
    {
        _contexts = contexts;
        _logger = logger;
        _parserFactory = parserFactory;
        _sse = sse;
        _enrichmentHandoff = enrichmentHandoff;
        _lifecycleObserver = lifecycleObserver;
    }

    public async Task ProcessImport(int importId, CancellationToken cancellationToken)
    {
        var outcome = await ProcessImportExecution(importId, 0, cancellationToken);
        if (outcome == LocationImportExecutionOutcome.Stale) return;
        await using var context = await _contexts.CreateDbContextAsync(CancellationToken.None);
        var import = await context.LocationImports.FindAsync([importId], CancellationToken.None);
        if (import is null) return;
        import.Status = outcome switch
        {
            LocationImportExecutionOutcome.Completed => ImportStatus.Completed,
            LocationImportExecutionOutcome.Failed => ImportStatus.Failed,
            _ => ImportStatus.Stopped
        };
        if (outcome == LocationImportExecutionOutcome.Failed) import.ErrorMessage = "Import processing failed.";
        await context.SaveChangesAsync(CancellationToken.None);
    }

    public async Task<LocationImportExecutionOutcome> ProcessImportExecution(
        int importId, int epoch, CancellationToken cancellationToken)
    {
        LocationImportSnapshot snapshot;
        await using (var context = await _contexts.CreateDbContextAsync(cancellationToken))
        {
            var import = await context.LocationImports.AsNoTracking()
                .SingleOrDefaultAsync(item => item.Id == importId, cancellationToken);
            if (!HasExecutionAuthority(import, epoch)) return LocationImportExecutionOutcome.Stale;
            snapshot = new(import.Id, import.UserId, import.FilePath, import.FileType,
                import.LastProcessedIndex, import.ExecutionEpoch);
        }

        try
        {
            var total = await CountLocationsAsync(snapshot, cancellationToken);
            if (!await SetTotalAsync(snapshot.Id, epoch, total, cancellationToken))
                return LocationImportExecutionOutcome.Stale;
            var processed = snapshot.LastProcessedIndex;
            var batch = new List<Location>(BatchSize);
            await using var stream = File.OpenRead(snapshot.FilePath);
            var parser = _parserFactory.GetParser(snapshot.FileType);
            var ordinal = 0;
            await foreach (var location in parser.ParseAsync(stream, snapshot.UserId, cancellationToken))
            {
                if (ordinal++ < processed) continue;
                location.Source ??= "queue-import";
                batch.Add(location);
                if (batch.Count < BatchSize) continue;
                var outcome = await PersistBatchAsync(snapshot, epoch, batch, processed, cancellationToken);
                if (outcome != LocationImportExecutionOutcome.Completed)
                    return await HandleBatchOutcomeAsync(snapshot, outcome, cancellationToken);
                processed += batch.Count;
                batch.Clear();
                await AfterBatchAsync(snapshot, epoch, processed, cancellationToken);
            }
            if (batch.Count > 0)
            {
                var outcome = await PersistBatchAsync(snapshot, epoch, batch, processed, cancellationToken);
                if (outcome != LocationImportExecutionOutcome.Completed)
                    return await HandleBatchOutcomeAsync(snapshot, outcome, cancellationToken);
                processed += batch.Count;
                await AfterBatchAsync(snapshot, epoch, processed, cancellationToken);
            }
            if (!await HasCurrentExecutionAuthorityAsync(snapshot.Id, epoch, cancellationToken))
                return LocationImportExecutionOutcome.Stale;
            await ReconcileEnrichmentAsync(snapshot, cancellationToken);
            await _sse.BroadcastAsync($"import-{snapshot.UserId}", SafeProgressEvent);
            _logger.LogInformation("Import {ImportId} completed successfully: {Total} records processed.", importId, total);
            return LocationImportExecutionOutcome.Completed;
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Import {ImportId} was cancelled mid-process.", importId);
            await ReconcileEnrichmentAsync(snapshot, CancellationToken.None);
            await _sse.BroadcastAsync($"import-{snapshot.UserId}", SafeProgressEvent);
            return LocationImportExecutionOutcome.Cancelled;
        }
        catch (DbUpdateConcurrencyException) { return LocationImportExecutionOutcome.Stale; }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error occurred while processing import {ImportId}.", importId);
            await _sse.BroadcastAsync($"import-{snapshot.UserId}", SafeProgressEvent);
            return LocationImportExecutionOutcome.Failed;
        }
    }

    private async Task<int> CountLocationsAsync(LocationImportSnapshot snapshot, CancellationToken token)
    {
        if (!File.Exists(snapshot.FilePath)) throw new FileNotFoundException($"Import file not found at: {snapshot.FilePath}");
        var count = 0;
        await using var stream = File.OpenRead(snapshot.FilePath);
        await foreach (var unused in _parserFactory.GetParser(snapshot.FileType)
            .ParseAsync(stream, snapshot.UserId, token)) count++;
        return count;
    }

    private async Task<bool> SetTotalAsync(int importId, int epoch, int total, CancellationToken token)
    {
        await using var context = await _contexts.CreateDbContextAsync(token);
        var import = await context.LocationImports.SingleOrDefaultAsync(item => item.Id == importId, token);
        if (!HasExecutionAuthority(import, epoch)) return false;
        import.TotalRecords = total;
        await context.SaveChangesAsync(token);
        return true;
    }

    private async Task<LocationImportExecutionOutcome> PersistBatchAsync(
        LocationImportSnapshot snapshot, int epoch, List<Location> batch, int processed, CancellationToken token)
    {
        await using var context = await _contexts.CreateDbContextAsync(token);
        await using var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(token) : null;
        if (context.Database.IsNpgsql() && batch.Any(location => !location.IdempotencyKey.HasValue))
            _ = await context.Users.FromSqlInterpolated($$"""
                SELECT * FROM "AspNetUsers" WHERE "Id" = {{snapshot.UserId}} FOR UPDATE
                """).SingleAsync(token);
        var import = await context.LocationImports.SingleOrDefaultAsync(item => item.Id == snapshot.Id, token);
        if (import is not null && import.Status == ImportStatus.Stopping && import.ExecutionEpoch == epoch &&
            import.DeletionRequestedAtUtc is null)
            return LocationImportExecutionOutcome.Cancelled;
        if (!HasExecutionAuthority(import, epoch)) return LocationImportExecutionOutcome.Stale;
        await ResolveActivityTypesAsync(context, batch, token);
        var batchKeys = batch.Where(item => item.IdempotencyKey.HasValue)
            .Select(item => item.IdempotencyKey!.Value).ToHashSet();
        var (toInsert, skipped) = await LocationImportDeduplicator.FilterAsync(
            context, batch, batchKeys, snapshot.UserId, _logger, token);
        import.SkippedDuplicates += skipped;
        if (toInsert.Count > 0)
        {
            var reused = await LocationImportDeduplicator.InsertAsync(context, toInsert, snapshot.UserId, token);
            import.SkippedDuplicates += reused;
            if (import.EnrichmentRequested)
                import.RemainingEnrichmentCount += toInsert.Count(item =>
                    context.Entry(item).State != EntityState.Detached && IsMissingAddress(item));
        }
        import.LastProcessedIndex = processed + batch.Count;
        var latest = batch.MaxBy(item => item.Timestamp);
        import.LastImportedRecord = latest is null ? "N/A" : $"Timestamp: {latest.Timestamp:u}" +
            (!string.IsNullOrWhiteSpace(latest.FullAddress) ? $", {latest.FullAddress}" : "");
        if (!await context.LocationImports.AsNoTracking().AnyAsync(item => item.Id == snapshot.Id &&
            item.Status == ImportStatus.InProgress && item.DeletionRequestedAtUtc == null &&
            item.StopRequestedAtUtc == null && (epoch == 0 || item.ExecutionEpoch == epoch), token))
            return LocationImportExecutionOutcome.Stale;
        await context.SaveChangesAsync(token);
        if (transaction is not null) await transaction.CommitAsync(token);
        return LocationImportExecutionOutcome.Completed;
    }

    private async Task<LocationImportExecutionOutcome> HandleBatchOutcomeAsync(
        LocationImportSnapshot snapshot, LocationImportExecutionOutcome outcome, CancellationToken token)
    {
        if (outcome == LocationImportExecutionOutcome.Cancelled)
            await ReconcileEnrichmentAsync(snapshot, token);
        return outcome;
    }

    private async Task AfterBatchAsync(LocationImportSnapshot snapshot, int epoch, int processed, CancellationToken token)
    {
        await _lifecycleObserver.AfterBatchCommittedAsync(snapshot.Id, epoch, processed, token);
        await ReconcileEnrichmentAsync(snapshot, token);
        await _sse.BroadcastAsync($"import-{snapshot.UserId}", SafeProgressEvent);
        await Task.Delay(1_000, token);
    }

    private static async Task ResolveActivityTypesAsync(
        ApplicationDbContext context, List<Location> locations, CancellationToken token)
    {
        if (!locations.Any(item => !string.IsNullOrWhiteSpace(item.ImportedActivityName))) return;
        var activities = await context.ActivityTypes.AsNoTracking()
            .Where(item => !string.IsNullOrWhiteSpace(item.Name)).ToListAsync(token);
        var lookup = activities.ToDictionary(item => item.Name!, item => item.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var location in locations)
        {
            var name = location.ImportedActivityName?.Trim();
            if (name is null || !lookup.TryGetValue(name, out var id)) continue;
            location.ActivityTypeId = id;
            location.ActivityType = null;
            location.ImportedActivityName = null;
        }
    }

    private async Task<bool> HasCurrentExecutionAuthorityAsync(int importId, int epoch, CancellationToken token)
    {
        await using var context = await _contexts.CreateDbContextAsync(token);
        return await context.LocationImports.AsNoTracking().AnyAsync(import => import.Id == importId &&
            import.Status == ImportStatus.InProgress && import.DeletionRequestedAtUtc == null &&
            import.StopRequestedAtUtc == null && (epoch == 0 || import.ExecutionEpoch == epoch), token);
    }

    private async Task ReconcileEnrichmentAsync(LocationImportSnapshot snapshot, CancellationToken token)
    {
        if (_enrichmentHandoff is null) return;
        await using (var context = await _contexts.CreateDbContextAsync(token))
        {
            if (!await context.LocationImports.AsNoTracking().AnyAsync(import => import.Id == snapshot.Id &&
                import.EnrichmentRequested && import.Status == ImportStatus.InProgress &&
                import.DeletionRequestedAtUtc == null && import.StopRequestedAtUtc == null &&
                import.ExecutionEpoch == snapshot.ExecutionEpoch, token)) return;
        }
        try { await _enrichmentHandoff.EnsureAsync(snapshot.UserId, token); }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Enrichment scheduling requires reconciliation after import {ImportId}.", snapshot.Id);
        }
    }

    private static bool IsMissingAddress(Location location) =>
        GeoapifyLocationBackfillService.IsWhollyUnenriched(location);

    private static bool HasExecutionAuthority([NotNullWhen(true)] LocationImport? import, int epoch) =>
        import is not null && import.Status == ImportStatus.InProgress && import.DeletionRequestedAtUtc is null &&
        import.StopRequestedAtUtc is null && (epoch == 0 || import.ExecutionEpoch == epoch);

    private sealed record LocationImportSnapshot(int Id, string UserId, string FilePath,
        LocationImportFileType FileType, int LastProcessedIndex, int ExecutionEpoch);

    private sealed class CloningContextFactory : IDbContextFactory<ApplicationDbContext>
    {
        private readonly DbContextOptions<ApplicationDbContext> _options;
        private readonly IServiceProvider _services = new ServiceCollection().BuildServiceProvider();
        private readonly ApplicationDbContext _source;

        internal CloningContextFactory(ApplicationDbContext source)
        {
            _source = source;
            _options = (DbContextOptions<ApplicationDbContext>)source.GetService<IDbContextOptions>();
        }

        public ApplicationDbContext CreateDbContext() => new RefreshingContext(_options, _services,
            () => _source.ChangeTracker.Clear());

        private sealed class RefreshingContext(DbContextOptions<ApplicationDbContext> options,
            IServiceProvider services, Action disposed) : ApplicationDbContext(options, services)
        {
            private int reported;
            public override void Dispose()
            {
                if (Interlocked.Exchange(ref reported, 1) == 0) disposed();
                base.Dispose();
            }
            public override async ValueTask DisposeAsync()
            {
                if (Interlocked.Exchange(ref reported, 1) == 0) disposed();
                await base.DisposeAsync();
            }
        }
    }
}
