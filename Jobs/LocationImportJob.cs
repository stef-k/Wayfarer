using Quartz;
using System;
using System.Threading;
using System.Threading.Tasks;
using Wayfarer.Parsers;
using Microsoft.Extensions.Logging;

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

        public LocationImportJob(
            ILocationImportService locationImportService,
            ILogger<LocationImportJob> logger)
        {
            _locationImportService = locationImportService;
            _logger = logger;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            // Pull the importId from the JobDataMap
            int importId = context.JobDetail.JobDataMap.GetInt("importId");
            CancellationToken ct = context.CancellationToken;

            _logger.LogInformation("Starting LocationImportJob for ImportId {ImportId}", importId);

            try
            {
                // Pass Quartz's CancellationToken into your service
                await _locationImportService.ProcessImport(importId, ct);

                _logger.LogInformation("Completed LocationImportJob for ImportId {ImportId}", importId);
            }
            catch (OperationCanceledException)
            {
                // Thrown if your service sees ct.IsCancellationRequested and throws
                _logger.LogInformation("LocationImportJob for ImportId {ImportId} was cancelled.", importId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in LocationImportJob for ImportId {ImportId}", importId);
                throw;
            }
        }
    }
}