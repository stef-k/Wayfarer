using Quartz;
using System;
using System.Threading;
using System.Threading.Tasks;
using Wayfarer.Parsers;
using Microsoft.Extensions.Logging;
using Wayfarer.Services.LocationImports;

namespace Wayfarer.Jobs
{
    // Prevent two concurrent executions of the same job key
    [DisallowConcurrentExecution]
    // If you ever update the JobDataMap in-flight, persist those changes
    [PersistJobDataAfterExecution]
    public class LocationImportJob : IJob
    {
        private readonly ILocationImportService _locationImportService;
        private readonly ILogger<LocationImportJob> _logger;
        private readonly ILocationImportLifecycle? _lifecycle;

        public LocationImportJob(
            ILocationImportService locationImportService,
            ILogger<LocationImportJob> logger,
            ILocationImportLifecycle? lifecycle = null)
        {
            _locationImportService = locationImportService;
            _logger = logger;
            _lifecycle = lifecycle;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            // Pull the importId from the JobDataMap
            int importId = context.JobDetail.JobDataMap.GetInt("importId");
            int epoch = context.JobDetail.JobDataMap.ContainsKey("epoch")
                && int.TryParse(context.JobDetail.JobDataMap.GetString("epoch"), out var parsedEpoch)
                ? parsedEpoch : 0;
            CancellationToken ct = context.CancellationToken;

            _logger.LogInformation("Starting LocationImportJob for ImportId {ImportId}", importId);

            try
            {
                var outcome = _locationImportService is ILocationImportExecutionService execution
                    ? await execution.ProcessImportExecution(importId, epoch, ct)
                    : await LegacyExecutionAsync(importId, ct);
                if (_lifecycle is not null)
                    await _lifecycle.ConvergeExecutionAsync(importId, epoch, outcome, CancellationToken.None);
                context.Result = outcome;

                _logger.LogInformation("Completed LocationImportJob for ImportId {ImportId}", importId);
            }
            catch (OperationCanceledException)
            {
                // Thrown if your service sees ct.IsCancellationRequested and throws
                _logger.LogInformation("LocationImportJob for ImportId {ImportId} was cancelled.", importId);
                if (_lifecycle is not null)
                    await _lifecycle.ConvergeExecutionAsync(importId, epoch, LocationImportExecutionOutcome.Cancelled, CancellationToken.None);
                context.Result = LocationImportExecutionOutcome.Cancelled;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in LocationImportJob for ImportId {ImportId}", importId);
                if (_lifecycle is not null)
                    await _lifecycle.ConvergeExecutionAsync(importId, epoch, LocationImportExecutionOutcome.Failed, CancellationToken.None);
                context.Result = LocationImportExecutionOutcome.Failed;
                throw;
            }
        }

        private async Task<LocationImportExecutionOutcome> LegacyExecutionAsync(int importId, CancellationToken token)
        {
            await _locationImportService.ProcessImport(importId, token);
            return LocationImportExecutionOutcome.Completed;
        }
    }
}
