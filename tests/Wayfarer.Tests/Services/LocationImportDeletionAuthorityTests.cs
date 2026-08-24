using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Wayfarer.Areas.Api.Controllers;
using Wayfarer.Models;
using Wayfarer.Models.Enums;
using Wayfarer.Parsers;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Services.LocationImports;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves stale queued workers cannot cross durable deletion authority.</summary>
public sealed class LocationImportDeletionAuthorityTests : TestBase
{
    [Fact]
    public async Task QueuedWorkerAfterDeletionIntent_PerformsNoImportOrEnrichmentWork()
    {
        await using var db = CreateDbContext();
        var handoff = new Mock<IImportEnrichmentHandoff>();
        var import = new LocationImport
        {
            Id = 511, UserId = "queued-owner", FileType = LocationImportFileType.Csv,
            FilePath = "missing-queued-upload.csv", Status = ImportStatus.Completed,
            ExecutionEpoch = 7, TotalRecords = 3, LastProcessedIndex = 2,
            EnrichmentRequested = true, DeletionRequestedAtUtc = DateTime.UtcNow
        };
        db.LocationImports.Add(import);
        await db.SaveChangesAsync();
        var sse = new RecordingSseService();
        var service = new LocationImportService(db,
            new ReverseGeocodingService(new HttpClient(new RejectingHandler()),
                NullLogger<BaseApiController>.Instance),
            NullLogger<LocationImportService>.Instance,
            new LocationDataParserFactory(NullLoggerFactory.Instance), sse, handoff.Object);

        var outcome = await service.ProcessImportExecution(import.Id, 7, CancellationToken.None);

        Assert.Equal(LocationImportExecutionOutcome.Stale, outcome);
        Assert.Empty(db.Locations);
        Assert.Equal(2, db.LocationImports.Single().LastProcessedIndex);
        Assert.Empty(sse.Messages);
        handoff.Verify(item => item.EnsureAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class RecordingSseService : SseService
    {
        internal List<string> Messages { get; } = [];
        public override Task BroadcastAsync(string channel, string data)
        { Messages.Add(data); return Task.CompletedTask; }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(new InvalidOperationException("Provider contact is forbidden."));
    }
}
