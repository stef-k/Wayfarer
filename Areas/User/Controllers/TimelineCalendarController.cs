using System.Data;
using System.Diagnostics;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.User.Controllers;

/// <summary>Explicit private calendar reads for W2 adoption; legacy chronology and bearer/public/Group routes keep their contracts.</summary>
[Area("User")]
[Authorize(AuthenticationSchemes = "Identity.Application", Roles = "User")]
[Route("User/Timeline/calendar")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class TimelineCalendarController(ApplicationDbContext db, IDataProtectionProvider protection,
    IOptions<JsonOptions> jsonOptions, ILogger<TimelineCalendarController> logger) : Controller
{
    /// <summary>One collection permit sequences selected markers, counts, summary, endpoint facts and navigation.</summary>
    [HttpGet("period")]
    public Task<IActionResult> Period(CancellationToken token) => ReadAsync("period", token);

    /// <summary>Deliberate geographic drill-down uses the same scope without selected-detail admission.</summary>
    [HttpGet("statistics")]
    public Task<IActionResult> Statistics(CancellationToken token) => ReadAsync("statistics", token);

    /// <summary>Independent unsampled summary; drawing is optional and cannot change the analytical source.</summary>
    [HttpGet("analysis")]
    public Task<IActionResult> Analysis(CancellationToken token) => ReadAsync("analysis", token);

    /// <summary>Private body-based period reads never put filter values in request URLs.</summary>
    [HttpPost("period"), ValidateAntiForgeryToken]
    public Task<IActionResult> PeriodPost(CancellationToken token) => ReadAsync("period", token, true);

    /// <summary>Filtered geographic drill-down uses the same antiforgery and owner boundary.</summary>
    [HttpPost("statistics"), ValidateAntiForgeryToken]
    public Task<IActionResult> StatisticsPost(CancellationToken token) => ReadAsync("statistics", token, true);

    /// <summary>Full owner-source analysis retains excluded intermediates.</summary>
    [HttpPost("analysis"), ValidateAntiForgeryToken]
    public Task<IActionResult> AnalysisPost(CancellationToken token) => ReadAsync("analysis", token, true);

    /// <summary>Compact known records reuse the existing protected continuation protocol.</summary>
    [HttpPost("records"), ValidateAntiForgeryToken]
    public Task<IActionResult> RecordsPost(CancellationToken token) => ReadAsync("records", token, true);

    /// <summary>On-demand whole-month marks remain independent of selected-range membership.</summary>
    [HttpPost("availability"), ValidateAntiForgeryToken]
    public Task<IActionResult> AvailabilityPost(CancellationToken token) => ReadAsync("availability", token, true);

    /// <summary>Owns one nonwaiting shared permit through complete buffered serialization and bounded snapshot cleanup.</summary>
    private async Task<IActionResult> ReadAsync(string operation, CancellationToken disconnect, bool form = false)
    {
        Response.OnStarting(() => { Response.Headers.CacheControl = "no-store"; return Task.CompletedTask; });
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(disconnect);
        var duration = TimeSpan.FromSeconds(15);
        var clock = Stopwatch.StartNew(); budget.CancelAfter(duration);
        var token = budget.Token;
        try
        {
            var owner = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (owner == null || !await db.Users.AsNoTracking().AnyAsync(user => user.Id == owner && user.IsActive, token))
                return StatusCode(401, new { code = "sessionEnded" });
            using var permit = ApiWorkAdmission.HistoryCollections.TryAcquire(owner, out var status);
            if (permit == null)
            {
                Response.Headers.RetryAfter = "5";
                throw new HistoryRequestException(status == 429 ? "historyOwnerAdmissionExceeded" : "historyAdmissionUnavailable", status);
            }
            IQueryCollection values = Request.Query;
            if (form)
            {
                if (Request.Query.Count != 0 || !Request.HasFormContentType
                    || !Request.ContentType!.StartsWith("application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                    throw new HistoryRequestException("parametersInvalid");
                var body = await Request.ReadFormAsync(token);
                values = new QueryCollection(body.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));
            }
            var scope = form ? TimelineCalendarScope.ParsePost(values, operation, SystemClock.Instance.GetCurrentInstant(), db.HistoryEnvironment)
                : TimelineCalendarScope.Parse(values, operation == "analysis", SystemClock.Instance.GetCurrentInstant(), db.HistoryEnvironment);
            var (rules, geometry) = TimelineConnectionRules.Parse(values);
            var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            var failed = false;
            try
            {
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
                var milliseconds = Math.Max(1, (int)(duration - clock.Elapsed).TotalMilliseconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('statement_timeout', {milliseconds}, true)", token);
                var asOf = await db.Database.SqlQueryRaw<DateTime>("SELECT transaction_timestamp() AS \"Value\"").SingleAsync(token);
                await new HistoryReadService(db, new HistoryCursor(protection)).PrepareQueryAsync(owner, scope.Query, token);
                var reads = new TimelineCalendarReadService(db);
                var result = operation switch
                {
                    "period" => await reads.PeriodAsync(owner, scope, asOf, token),
                    "statistics" => await reads.DetailsAsync(owner, scope, asOf, token),
                    "records" => await reads.RecordsAsync(owner, scope, new HistoryCursor(protection), asOf, token),
                    "availability" => await reads.AvailabilityAsync(owner, scope, asOf, token),
                    _ => await reads.AnalysisAsync(owner, scope, rules, geometry, asOf, token)
                };
                token.ThrowIfCancellationRequested();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(result, jsonOptions.Value.JsonSerializerOptions);
                token.ThrowIfCancellationRequested();
                return File(bytes, "application/json; charset=utf-8");
            }
            catch { failed = true; throw; }
            finally { await HistoryReadSnapshot.EndAsync(transaction, failed); }
        }
        catch (HistoryQualificationException) { return Failure(503, "historyQualificationRequired"); }
        catch (HistoryRequestException failure) { return Failure(failure.Status, failure.Code); }
        catch (OperationCanceledException) when (disconnect.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { return Failure(503, "historyBudgetExceeded"); }
        catch (PostgresException failure) when (failure.SqlState is "57014" or "25P04" or "53200" or "53400")
        { return Failure(503, "historyBudgetExceeded"); }
        catch (NpgsqlException failure) when (failure.IsTransient) { return Failure(503, "historyStoreUnavailable"); }
        catch (Exception failure)
        {
            logger.LogError("Private calendar read failed ({FailureType}).", failure.GetType().Name);
            return Failure(500, "historyReadFailed");
        }
    }

    /// <summary>Fixed diagnostics contain no private source, query, identifier, credentials or database text.</summary>
    private ObjectResult Failure(int status, string code)
    {
        if (status == 503) Response.Headers.RetryAfter = "5";
        return StatusCode(status, new { code,
            message = status == 400 ? "Invalid calendar parameters." : "Calendar read unavailable; retain the selected scope and retry deliberately." });
    }
}
