using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Services;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationImports;
using Wayfarer.Services.LocationProviders;

namespace Wayfarer.Parsers;

/// <summary>Requires callers to supply their execution generation explicitly.</summary>
public interface ILocationImportService
{
    /// <summary>Processes and converges only the supplied durable execution generation.</summary>
    Task ProcessImport(int importId, int epoch, CancellationToken cancellationToken);
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
    private readonly LocationImportStagedFiles _stagedFiles;
    private readonly ILogger<LocationImportService> _logger;
    private readonly LocationDataParserFactory _parserFactory;
    private readonly SseService _sse;
    private readonly IImportEnrichmentHandoff? _enrichmentHandoff;
    private readonly ILocationImportLifecycleObserver _lifecycleObserver;

    /// <summary>Streams only files resolved through the shared staged-file authority.</summary>
    public LocationImportService(LocationImportStagedFiles stagedFiles, IDbContextFactory<ApplicationDbContext> contexts,
        ReverseGeocodingService reverseGeocodingService, ILogger<LocationImportService> logger,
        LocationDataParserFactory parserFactory, SseService sse,
        IImportEnrichmentHandoff? enrichmentHandoff = null)
        : this(stagedFiles, contexts, reverseGeocodingService, logger, parserFactory, sse, enrichmentHandoff,
            NullLocationImportLifecycleObserver.Instance)
    { }

    /// <summary>Retains source compatibility for focused tests while production uses a factory.</summary>
    internal LocationImportService(LocationImportStagedFiles stagedFiles, ApplicationDbContext context,
        ReverseGeocodingService reverseGeocodingService, ILogger<LocationImportService> logger,
        LocationDataParserFactory parserFactory, SseService sse,
        IImportEnrichmentHandoff? enrichmentHandoff = null)
        : this(stagedFiles, new CloningContextFactory(context), reverseGeocodingService, logger, parserFactory, sse,
            enrichmentHandoff, NullLocationImportLifecycleObserver.Instance)
    { }

    /// <summary>Retains observer-enabled source compatibility for focused tests.</summary>
    internal LocationImportService(LocationImportStagedFiles stagedFiles, ApplicationDbContext context,
        ReverseGeocodingService reverseGeocodingService, ILogger<LocationImportService> logger,
        LocationDataParserFactory parserFactory, SseService sse,
        IImportEnrichmentHandoff? enrichmentHandoff, ILocationImportLifecycleObserver lifecycleObserver)
        : this(stagedFiles, new CloningContextFactory(context), reverseGeocodingService, logger, parserFactory, sse,
            enrichmentHandoff, lifecycleObserver)
    { }

    /// <summary>Creates a worker with a test-controlled lifecycle observer.</summary>
    internal LocationImportService(LocationImportStagedFiles stagedFiles, IDbContextFactory<ApplicationDbContext> contexts,
        ReverseGeocodingService reverseGeocodingService, ILogger<LocationImportService> logger,
        LocationDataParserFactory parserFactory, SseService sse,
        IImportEnrichmentHandoff? enrichmentHandoff, ILocationImportLifecycleObserver lifecycleObserver)
    {
        _contexts = contexts;
        _stagedFiles = stagedFiles;
        _logger = logger;
        _parserFactory = parserFactory;
        _sse = sse;
        _enrichmentHandoff = enrichmentHandoff;
        _lifecycleObserver = lifecycleObserver;
    }

    /// <summary>Retains compatibility execution with an explicit epoch and fenced terminal persistence.</summary>
    public async Task ProcessImport(int importId, int epoch, CancellationToken cancellationToken)
    {
        var outcome = await ProcessImportExecution(importId, epoch, cancellationToken);
        if (outcome == LocationImportExecutionOutcome.Stale) return;
        await using var context = await _contexts.CreateDbContextAsync(CancellationToken.None);
        var import = await context.LocationImports.FindAsync([importId], CancellationToken.None);
        if (import is null || import.ExecutionEpoch != epoch || import.DeletionRequestedAtUtc.HasValue
            || (import.Status != ImportStatus.InProgress && import.Status != ImportStatus.Stopping)) return;
        if (import.Status == ImportStatus.Stopping) outcome = LocationImportExecutionOutcome.Cancelled;
        import.Status = outcome switch
        {
            LocationImportExecutionOutcome.Completed => ImportStatus.Completed,
            LocationImportExecutionOutcome.Failed or LocationImportExecutionOutcome.StagedFileUnavailable =>
                ImportStatus.Failed,
            _ => ImportStatus.Stopped
        };
        if (outcome is LocationImportExecutionOutcome.Failed or LocationImportExecutionOutcome.StagedFileUnavailable)
            import.ErrorMessage = outcome == LocationImportExecutionOutcome.StagedFileUnavailable
                ? "Import staged file unavailable." : "Import processing failed.";
        import.ProjectionPending = false;
        try { await context.SaveChangesAsync(CancellationToken.None); }
        catch (DbUpdateConcurrencyException) { } // A later lifecycle command retains authority.
    }

    /// <summary>Admits and commits only an exact, nonnegative durable execution generation.</summary>
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
            await using var stream = OpenStagedFile(snapshot.FilePath);
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
        catch (StagedFileUnavailableException)
        {
            _logger.LogError("Location import failed; code {Code}; import {ImportId}.",
                "location-import-file-unavailable", importId);
            await _sse.BroadcastAsync($"import-{snapshot.UserId}", SafeProgressEvent);
            return LocationImportExecutionOutcome.StagedFileUnavailable;
        }
        catch (DbUpdateConcurrencyException) { return LocationImportExecutionOutcome.Stale; }
        catch (Exception)
        {
            _logger.LogError("Location import failed; code {Code}; import {ImportId}.",
                "location-import-processing-failed", importId);
            await _sse.BroadcastAsync($"import-{snapshot.UserId}", SafeProgressEvent);
            return LocationImportExecutionOutcome.Failed;
        }
    }

    private async Task<int> CountLocationsAsync(LocationImportSnapshot snapshot, CancellationToken token)
    {
        // OpenStagedFile resolves authority before any filesystem access.
        var count = 0;
        await using var stream = OpenStagedFile(snapshot.FilePath);
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

    /// <summary>Bounds fresh whole-batch attempts and distinguishes deadline failure from user cancellation.</summary>
    private async Task<LocationImportExecutionOutcome> PersistBatchAsync(
        LocationImportSnapshot snapshot, int epoch, List<Location> batch, int processed, CancellationToken token)
    {
        // The deadline remains independent of user cancellation so an uncertain commit can be read durably.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var operations = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    operations.Token.ThrowIfCancellationRequested();
                    foreach (var location in batch)
                    {
                        location.Id = 0;
                        location.ActivityTypeId = null;
                        location.ActivityType = null;
                    }
                    return await PersistBatchAttemptAsync(snapshot, epoch, batch, processed, operations.Token);
                }
                catch (UnconfirmedBatchCommitException)
                {
                    // Never blindly retry a COMMIT whose server result was not observed.
                    return await RecoverBatchCommitAsync(snapshot.Id, epoch, processed + batch.Count, deadline.Token);
                }
                catch (Exception exception) when (attempt < 2 && IsRetryableBatchFailure(exception)
                    && !operations.IsCancellationRequested)
                {
                    // Attempt disposal has rolled back all provisional rows, catalog writes and facts.
                    await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), operations.Token);
                }
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Location import batch deadline exhausted.");
        }
    }

    /// <summary>Owns one fresh context/transaction, including Location winner arbitration before catalog writes.</summary>
    private async Task<LocationImportExecutionOutcome> PersistBatchAttemptAsync(
        LocationImportSnapshot snapshot, int epoch, List<Location> batch, int processed, CancellationToken token)
    {
        await using var context = await _contexts.CreateDbContextAsync(token);
        var transaction = context.Database.IsRelational()
            ? await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, token) : null;
        Exception? failure = null;
        try
        {
            var import = await context.LocationImports.SingleOrDefaultAsync(item => item.Id == snapshot.Id, token);
            if (import is not null && import.Status == ImportStatus.Stopping && import.ExecutionEpoch == epoch &&
                import.DeletionRequestedAtUtc is null)
                return LocationImportExecutionOutcome.Cancelled;
            if (!HasExecutionAuthority(import, epoch) || import.LastProcessedIndex != processed)
                return LocationImportExecutionOutcome.Stale;
            if (context.Database.IsNpgsql() && batch.Any(location => !location.IdempotencyKey.HasValue))
                _ = await context.Users.FromSqlInterpolated($$"""
                    SELECT * FROM "AspNetUsers" WHERE "Id" = {{snapshot.UserId}} FOR UPDATE
                    """).SingleAsync(token);
            var batchKeys = batch.Where(item => item.IdempotencyKey.HasValue)
                .Select(item => item.IdempotencyKey!.Value).ToHashSet();
            var (toInsert, skipped) = await LocationImportDeduplicator.FilterAsync(
                context, batch, batchKeys, snapshot.UserId, _logger, token);
            var reused = await LocationImportDeduplicator.InsertAsync(context, toInsert, snapshot.UserId, token);
            var accepted = toInsert.Where(item => context.Entry(item).State != EntityState.Detached).ToList();
            import.UnclassifiedActivityCount += await new ActivityCatalogReconciler(context).ReconcileAsync(accepted, token);
            import.SkippedDuplicates += skipped + reused;
            if (import.EnrichmentRequested)
                import.RemainingEnrichmentCount += accepted.Count(IsMissingAddress);
            import.LastProcessedIndex = processed + batch.Count;
            var latest = batch.MaxBy(item => item.Timestamp);
            import.LastImportedRecord = latest is null ? "N/A" : $"Timestamp: {latest.Timestamp:u}" +
                (!string.IsNullOrWhiteSpace(latest.FullAddress) ? $", {latest.FullAddress}" : "");
            if (!await context.LocationImports.AsNoTracking().AnyAsync(item => item.Id == snapshot.Id &&
                item.Status == ImportStatus.InProgress && item.DeletionRequestedAtUtc == null &&
                item.StopRequestedAtUtc == null && item.ExecutionEpoch == epoch, token))
                return LocationImportExecutionOutcome.Stale;
            await context.SaveChangesAsync(token);
            if (transaction is not null)
            {
                try { await transaction.CommitAsync(token); }
                catch (Exception exception) { throw new UnconfirmedBatchCommitException(exception); }
            }
            return LocationImportExecutionOutcome.Completed;
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                try { await transaction.DisposeAsync(); }
                catch (Exception) when (failure is not null)
                {
                    // Retain the primary failure; closing the context also discards its provisional state.
                    _logger.LogWarning("Location import rollback cleanup failed; code {Code}; import {ImportId}.",
                        "location-import-rollback-cleanup-failed", snapshot.Id);
                }
            }
        }
    }

    /// <summary>Classifies only bounded transient database contention; input and schema/storage failures never qualify.</summary>
    private static bool IsRetryableBatchFailure(Exception exception) => exception switch
    {
        ActivityCatalogResolutionException => true,
        PostgresException postgres => postgres.SqlState is PostgresErrorCodes.LockNotAvailable
            or PostgresErrorCodes.QueryCanceled or PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure,
        DbUpdateException { InnerException: PostgresException postgres } => IsRetryableBatchFailure(postgres),
        // Npgsql's nonretrying EF execution strategy wraps its transient PostgreSQL errors for callers.
        InvalidOperationException { InnerException: PostgresException postgres } => IsRetryableBatchFailure(postgres),
        _ => false
    };

    /// <summary>Uses the atomic durable checkpoint to confirm a commit, retaining exact generation authority.</summary>
    private async Task<LocationImportExecutionOutcome> RecoverBatchCommitAsync(
        int importId, int epoch, int expectedCheckpoint, CancellationToken token)
    {
        try
        {
            await using var context = await _contexts.CreateDbContextAsync(token);
            var import = await context.LocationImports.AsNoTracking().SingleOrDefaultAsync(i => i.Id == importId, token);
            if (!HasExecutionAuthority(import, epoch)) return LocationImportExecutionOutcome.Stale;
            return import.LastProcessedIndex == expectedCheckpoint
                ? LocationImportExecutionOutcome.Completed : LocationImportExecutionOutcome.Failed;
        }
        catch (Exception) { return LocationImportExecutionOutcome.Failed; } // Leave durable authority for owner resume.
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

    private async Task<bool> HasCurrentExecutionAuthorityAsync(int importId, int epoch, CancellationToken token)
    {
        await using var context = await _contexts.CreateDbContextAsync(token);
        return await context.LocationImports.AsNoTracking().AnyAsync(import => import.Id == importId &&
            import.Status == ImportStatus.InProgress && import.DeletionRequestedAtUtc == null &&
            import.StopRequestedAtUtc == null && import.ExecutionEpoch == epoch, token);
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
        catch (Exception)
        {
            _logger.LogWarning("Location import enrichment handoff requires reconciliation; code {Code}; " +
                "import {ImportId}.", "location-import-enrichment-reconciliation-required", snapshot.Id);
        }
    }

    private static bool IsMissingAddress(Location location) =>
        GeoapifyLocationBackfillService.IsWhollyUnenriched(location);

    /// <summary>Opens durable file authority while translating path-bearing absence exceptions.</summary>
    private FileStream OpenStagedFile(string reference)
    {
        if (!_stagedFiles.TryResolve(reference, out var filePath)) throw new StagedFileUnavailableException();
        try { return File.OpenRead(filePath); }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new StagedFileUnavailableException();
        }
    }

    private static bool HasExecutionAuthority([NotNullWhen(true)] LocationImport? import, int epoch) =>
        import is not null && import.Status == ImportStatus.InProgress && import.DeletionRequestedAtUtc is null &&
        import.StopRequestedAtUtc is null && epoch >= 0 && import.ExecutionEpoch == epoch;

    private sealed record LocationImportSnapshot(int Id, string UserId, string FilePath,
        LocationImportFileType FileType, int LastProcessedIndex, int ExecutionEpoch);

    /// <summary>Classifies absent staged authority without carrying its private path.</summary>
    private sealed class StagedFileUnavailableException : Exception;

    /// <summary>Prevents transient-failure retry from guessing whether COMMIT reached PostgreSQL.</summary>
    private sealed class UnconfirmedBatchCommitException(Exception inner) : Exception("Import commit requires durable confirmation.", inner);

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
