using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Wayfarer.Middleware;
using Wayfarer.Services.LocationImports;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Util;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Bearer import and shared enrichment adapter. Full capability remains gated on package-4 integration qualification.</summary>
[ApiController, Area("Api"), Route("api/mobile/location-imports"), ManagedImport]
public sealed class MobileLocationImportsController(ManagedImportUpload upload, ManagedImportStatus status,
    ManagedImportLifecycle lifecycle, Wayfarer.Parsers.IApplicationSettingsService settings,
    IOptions<ManagedImportOptions> options, IImportEnrichmentHandoff enrichment,
    ILocationEnrichmentPresentationProjector presentation) : ControllerBase
{
    private ManagedImportMiddleware.Admission Owner => (ManagedImportMiddleware.Admission)HttpContext.Items[ManagedImportMiddleware.RequestKey]!;

    /// <summary>Advertises incomplete integration explicitly; selecting enrichment never silently changes intent.</summary>
    [HttpGet("capabilities")]
    public IActionResult Capabilities()
    {
        var limit = UploadRequestPolicy.EffectiveBytes(settings.GetSettings().UploadSizeLimitMB);
        return Ok(new { contract = ManagedImportStatus.Contract, subjectId = Owner.UserId,
            ready = false, reason = "integration_qualification_pending", uploadsEnabled = false,
            managedIntakeConfigured = OperatingSystem.IsLinux() && options.Value.LocalStoreQualified && limit > 0,
            formats = new[] { "csv", "wayfarer-geojson" }, maxRequestBytes = limit, maxFileBytes = limit,
            uploadDeadlineSeconds = 600, uploadLeaseSeconds = 60, maxConcurrentUploadsPerOwner = 1,
            maxConcurrentExecutionsPerOwner = 1, maxRetainedResumableFilesPerOwner = 3,
            maxManagedBytesPerOwner = 3 * limit, maxManagedBytesInstallation = 10L * 1024 * 1024 * 1024,
            completedFileRetentionSeconds = 86400, resumableFileExpiry = (DateTime?)null, pollAfterSeconds = 5,
            receiptRetention = "account-lifetime", jobRetention = "completed-detail-90-days-then-request-tombstone",
            enrichment = new { scope = "account", available = true,
                disclosure = "Includes older eligible Locations and spends the same account provider budget. Import completion is separate from enrichment.",
                statusUrl = "/api/mobile/location-imports/enrichment" },
            reconciliation = new { keyStatusMaxKeys = 100, detailProfile = "owner-detail-1", perOriginalImportReceipts = false } });
    }

    /// <summary>Raw bytes, never multipart. Accepted replay is resolved by middleware before this upload-policy boundary.</summary>
    [HttpPut("uploads/{requestId}"), ManagedImportUpload, UserFileUpload]
    public async Task<IActionResult> Put(CancellationToken token)
    {
        var fingerprint = Owner.Fingerprint!;
        var expected = fingerprint.Format == "csv" ? "text/csv" : "application/geo+json";
        if (!System.Net.Http.Headers.MediaTypeHeaderValue.TryParse(Request.ContentType, out var media)
            || !string.Equals(media.MediaType, expected, StringComparison.OrdinalIgnoreCase)
            || Request.Headers.ContentEncoding.Count != 0)
            throw new ManagedImportException(415, "unsupported_media_type");
        var limit = UploadRequestPolicy.EffectiveBytes(settings.GetSettings().UploadSizeLimitMB);
        var accepted = await upload.AcceptAsync(Owner.UserId, Owner.Credential, Owner.RequestId!.Value,
            fingerprint, limit, Request.Body, token);
        var row = await status.UploadAsync(Owner.UserId, Owner.RequestId.Value, token)
            ?? throw new ManagedImportException(503, "import_outcome_unknown");
        if (accepted.Created) Response.Headers.Location = $"/api/mobile/location-imports/{row.ImportId}";
        return StatusCode(accepted.Created ? 201 : ManagedImportStatus.HttpStatus(row), ManagedImportStatus.Project(row));
    }

    /// <summary>Recovers exact owner/request identity, including cleanup and retired accepted results.</summary>
    [HttpGet("uploads/{requestId:guid}")]
    public async Task<IActionResult> Upload(Guid requestId, CancellationToken token)
    {
        var row = await status.UploadAsync(Owner.UserId, requestId, token)
            ?? throw new ManagedImportException(404, "import_not_found");
        return StatusCode(ManagedImportStatus.HttpStatus(row), ManagedImportStatus.Project(row));
    }

    /// <summary>Owner-only managed job; Web-only or foreign jobs share 404.</summary>
    [HttpGet("{id:int}")]
    public async Task<IActionResult> Get(int id, CancellationToken token)
    {
        var row = await status.ImportAsync(Owner.UserId, id, token) ?? throw new ManagedImportException(404, "import_not_found");
        return Ok(ManagedImportStatus.Project(row));
    }

    /// <summary>Target epoch is command identity; replay observes current outcome without advancing.</summary>
    [HttpPost("{id:int}/start")]
    public async Task<IActionResult> Start(int id, EpochCommand command, CancellationToken token)
    {
        if (command.TargetEpoch is not > 0) throw new ManagedImportException(400, "invalid_execution_epoch");
        var created = await lifecycle.StartAsync(Owner.UserId, id, command.TargetEpoch, Owner.Credential, token);
        var row = await status.ImportAsync(Owner.UserId, id, token) ?? throw new ManagedImportException(503, "import_outcome_unknown");
        return StatusCode(created || row.Import!.ProjectionPending ? 202 : 200, ManagedImportStatus.Project(row));
    }

    /// <summary>Exact current-generation Stop cannot stop a subsequent resumed epoch.</summary>
    [HttpPost("{id:int}/stop")]
    public async Task<IActionResult> Stop(int id, EpochCommand command, CancellationToken token)
    {
        if (command.ExecutionEpoch is not > 0) throw new ManagedImportException(400, "invalid_execution_epoch");
        var pending = await lifecycle.StopAsync(Owner.UserId, id, command.ExecutionEpoch, Owner.Credential, token);
        var row = await status.ImportAsync(Owner.UserId, id, token) ?? throw new ManagedImportException(503, "import_outcome_unknown");
        return StatusCode(pending ? 202 : 200, ManagedImportStatus.Project(row));
    }

    /// <summary>Payload/history deletion retains accepted identity and never deletes imported Locations.</summary>
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, CancellationToken token) =>
        await lifecycle.DeleteAsync(Owner.UserId, id, Owner.Credential, token) ? NoContent() : StatusCode(202, new { code = "import_deletion_pending" });

    /// <summary>Credential-free account-wide status; counters are cumulative and never attributed to this file.</summary>
    [HttpGet("enrichment")]
    public async Task<IActionResult> Enrichment(CancellationToken token)
    {
        using var budget = ManagedImportAuthority.Budget(token);
        return Ok(await presentation.ProjectAsync(Owner.UserId, budget.Token));
    }

    /// <summary>Explicit account-wide intent with snapshot preconditions enforced inside the shared locked owner.</summary>
    [HttpPost("enrichment/{control}")]
    public async Task<IActionResult> Control(string control, EnrichmentCommand command, CancellationToken token)
    {
        var action = control switch
        {
            "start" => EnrichmentControl.Start, "pause" => EnrichmentControl.Pause,
            "resume" => EnrichmentControl.Resume, "cancel" => EnrichmentControl.Cancel,
            _ => throw new ManagedImportException(404, "enrichment_control_not_found")
        };
        if (command.ExpectedEpoch is not >= 0 || !Enum.TryParse<LocationEnrichmentState>(command.ExpectedState, out var state)
            || !Enum.IsDefined(state) || state.ToString() != command.ExpectedState)
            throw new ManagedImportException(400, "invalid_enrichment_precondition");
        using var budget = ManagedImportAuthority.Budget(token);
        var result = await enrichment.ControlAsync(Owner.UserId, action,
            new(command.ExpectedEpoch.Value, state, Owner.Credential), budget.Token);
        var httpStatus = result.Classification switch
        {
            LocationEnrichmentCommandResult.Applied or LocationEnrichmentCommandResult.AlreadySatisfied => 200,
            LocationEnrichmentCommandResult.SchedulingPending => 202,
            LocationEnrichmentCommandResult.AuthorityUnavailable => 503,
            _ => 409
        };
        return StatusCode(httpStatus, new { classification = result.Classification.ToString(), code = result.Code,
            durableIntentSaved = result.Succeeded || result.Classification == LocationEnrichmentCommandResult.SchedulingPending,
            schedulingPending = result.Classification == LocationEnrichmentCommandResult.SchedulingPending,
            statusUrl = "/api/mobile/location-imports/enrichment", recovery = "read_status_then_new_intent" });
    }

    /// <summary>A snapshot precondition is not a replayable command identity; unknown Start/Resume needs fresh intent.</summary>
    public sealed record EnrichmentCommand(int? ExpectedEpoch, string? ExpectedState);

    /// <summary>Only the addressed epoch is accepted; no general command ledger.</summary>
    public sealed record EpochCommand(int? TargetEpoch, int? ExecutionEpoch);
}
