using Quartz;
using System;
using System.Threading;
using System.Threading.Tasks;
using Wayfarer.Parsers;
using Microsoft.Extensions.Logging;
using Wayfarer.Services.LocationImports;

namespace Wayfarer.Jobs
{
    /// <summary>Admits only jobs carrying a verifiable explicit execution generation.</summary>
    // Prevent two concurrent executions of the same job key
    [DisallowConcurrentExecution]
    // If you ever update the JobDataMap in-flight, persist those changes
    [PersistJobDataAfterExecution]
    public class LocationImportJob : IJob
    {
        private readonly ILocationImportService _locationImportService;
        private readonly ILogger<LocationImportJob> _logger;
        private readonly ILocationImportLifecycle? _lifecycle;

        /// <summary>Uses durable worker authority and optional lifecycle convergence.</summary>
        public LocationImportJob(
            ILocationImportService locationImportService,
            ILogger<LocationImportJob> logger,
            ILocationImportLifecycle? lifecycle = null)
        {
            _locationImportService = locationImportService;
            _logger = logger;
            _lifecycle = lifecycle;
        }

        /// <summary>Rejects unverifiable or conflicting job authority before invoking persistence or convergence.</summary>
        public async Task Execute(IJobExecutionContext context)
        {
            if (!LocationImportSchedulerKeys.TryReadExecution(context.JobDetail, out var importId, out var epoch))
            {
                context.Result = LocationImportExecutionOutcome.Stale;
                return;
            }
            CancellationToken ct = context.CancellationToken;

            _logger.LogInformation("Starting LocationImportJob for ImportId {ImportId}", importId);

            try
            {
                var outcome = _locationImportService is ILocationImportExecutionService execution
                    ? await execution.ProcessImportExecution(importId, epoch, ct)
                    : await LegacyExecutionAsync(importId, epoch, ct);
                if (_lifecycle is not null)
                    outcome = await _lifecycle.ConvergeExecutionAsync(
                        importId, epoch, outcome, CancellationToken.None);
                context.Result = outcome;

                _logger.LogInformation("Completed LocationImportJob for ImportId {ImportId}", importId);
            }
            catch (OperationCanceledException)
            {
                // Thrown if your service sees ct.IsCancellationRequested and throws
                _logger.LogInformation("LocationImportJob for ImportId {ImportId} was cancelled.", importId);
                if (_lifecycle is not null)
                    context.Result = await _lifecycle.ConvergeExecutionAsync(importId, epoch,
                        LocationImportExecutionOutcome.Cancelled, CancellationToken.None);
                else context.Result = LocationImportExecutionOutcome.Cancelled;
            }
            catch (Exception)
            {
                _logger.LogError("Location import job failed; code {Code}; import {ImportId}.",
                    "location-import-job-failed", importId);
                if (_lifecycle is not null)
                    context.Result = await _lifecycle.ConvergeExecutionAsync(importId, epoch,
                        LocationImportExecutionOutcome.Failed, CancellationToken.None);
                else context.Result = LocationImportExecutionOutcome.Failed;
                throw;
            }
        }

        /// <summary>Passes the admitted generation through the compatibility service contract.</summary>
        private async Task<LocationImportExecutionOutcome> LegacyExecutionAsync(int importId, int epoch, CancellationToken token)
        {
            await _locationImportService.ProcessImport(importId, epoch, token);
            return LocationImportExecutionOutcome.Completed;
        }
    }
}
