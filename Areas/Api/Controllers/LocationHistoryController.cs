using System.Data;
using System.Security.Claims;
using Wayfarer.Services.LocationMutations;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Services;
using Wayfarer.Util;

namespace Wayfarer.Areas.Api.Controllers;

/// <summary>Active-owner bearer personal history. No provider dependencies, ingestion or read-time repair.</summary>
[ApiController]
[Area("Api")]
[Route("api/location/history")]
public sealed class LocationHistoryController(ApplicationDbContext db, IDataProtectionProvider protection,
    IOptions<JsonOptions> jsonOptions) : ControllerBase
{
    // Separate process pools: selected detail never waits for a list or overview permit.
    internal static ApiWorkAdmission Collections { get; } = new(4, 2);
    internal static ApiWorkAdmission Details { get; } = new(16, 4);

    /// <summary>Returns an unsampled bounded page, or an independently paged conservative candidate inspection.</summary>
    [HttpGet]
    public Task<IActionResult> History(CancellationToken token) => ReadAsync(false, null, token);

    /// <summary>Aggregates all definite matches independently of chronology, retaining every occupied cell.</summary>
    [HttpGet("overview")]
    public Task<IActionResult> Overview(CancellationToken token) => ReadAsync(true, null, token);

    /// <summary>Returns complete stored owner detail, independently of browsing filters and preparation gates.</summary>
    [HttpGet("{locationId:int}")]
    public Task<IActionResult> Detail(int locationId, CancellationToken token) => ReadAsync(false, locationId, token);

    /// <summary>Authorizes at request time, then owns bounded snapshot evaluation through buffered serialization.</summary>
    private async Task<IActionResult> ReadAsync(bool overview, int? id, CancellationToken disconnect)
    {
        Response.OnStarting(() => { Response.Headers.CacheControl = "no-store"; return Task.CompletedTask; });
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(disconnect);
        // The collection ceiling is protective; the sub-second performance target is qualified separately.
        var duration = id == null ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(30);
        var clock = Stopwatch.StartNew();
        budget.CancelAfter(duration);
        var token = budget.Token;
        try
        {
            ApplicationUser? user;
            if (id != null && ActiveAccountCookie.HasCookieAuthority(HttpContext))
            {
                var owner = User.Identity?.IsAuthenticated == true ? User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
                user = await db.Users.AsNoTracking().SingleOrDefaultAsync(row => row.Id == owner && row.IsActive, token);
            }
            else
            {
                var values = Request.Headers.Authorization;
                if (values.Count != 1 || !AuthenticationHeaderValue.TryParse(values[0], out var authorization)
                    || !string.Equals(authorization.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrWhiteSpace(authorization.Parameter) || authorization.Parameter.Any(char.IsWhiteSpace)) return ProtectedStatus(401);
                user = await IncomingApiTokenResolver.ForRequest(HttpContext, db)
                    .ResolveTokenAsync(authorization.Parameter, RateLimitHelper.GetClientIpAddress(HttpContext), token);
            }
            if (user is not { IsActive: true }) return ProtectedStatus(401);
            if (id != null && Request.Headers.ContainsKey("X-Location-Base"))
            {
                var proof = LocationMutationBase.Read(protection, Request.Headers["X-Location-Base"], user.Id, id.Value);
                if (proof is { Uuid: null })
                {
                    await using var fence = await LocationDeletionFence.BeginAsync(db, user.Id, token);
                    if (await LocationMutationAuthority.RevalidateAsync(HttpContext, db, user.Id, token)
                        && !await db.Locations.AnyAsync(row => row.Id == id.Value, token))
                        return StatusCode(404, new { contract = "location-target-absence-1", subjectId = user.Id, locationId = id.Value,
                            uuid = (Guid?)null, baseId = proof.BaseId, state = "absent", observedAtUtc = await LocationMutationService.NowAsync(db, token) });
                }
            }
            using var permit = (id == null ? Collections : Details).TryAcquire(user.Id, out var status);
            if (permit == null)
            {
                Response.Headers.RetryAfter = "5";
                throw new HistoryRequestException(status == 429 ? "historyOwnerAdmissionExceeded" : "historyAdmissionUnavailable", status);
            }
            var query = id == null ? HistoryQuery.Parse(Request.Query, overview, db.HistoryEnvironment) : null;
            var zone = id != null ? HistoryQuery.DetailZone(Request.Query) : query!.Zone;
            return await ReadSnapshotAsync(user.Id, overview, id, query, zone, duration - clock.Elapsed, token);
        }
        catch (HistoryQualificationException)
        { return StatusCode(503, new { code = "historyQualificationRequired", message = "History environment qualification is required; rebuilding cannot qualify it." }); }
        catch (HistoryRequestException failure)
        { return StatusCode(failure.Status, new { code = failure.Code, message = Message(failure.Code) }); }
        catch (OperationCanceledException) when (disconnect.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        { return StatusCode(503, new { code = "historyBudgetExceeded", message = "History read budget exceeded." }); }
        catch (PostgresException failure) when (failure.SqlState is "57014" or "25P04" or "53200" or "53400")
        { return StatusCode(503, new { code = "historyBudgetExceeded", message = "History read budget exceeded." }); }
        catch (NpgsqlException failure) when (failure.IsTransient)
        { return StatusCode(503, new { code = "historyStoreUnavailable", message = "History store is temporarily unavailable." }); }
    }

    /// <summary>Buffer a complete snapshot before transfer; cleanup preserves primary failures and has its own short bound.</summary>
    private async Task<IActionResult> ReadSnapshotAsync(string owner, bool overview, int? id, HistoryQuery? query,
        NodaTime.DateTimeZone zone, TimeSpan remaining, CancellationToken token)
    {
        var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
        var failed = false;
        try
        {
            await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", token);
            var remainingMs = Math.Max(1, (int)remaining.TotalMilliseconds);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT set_config('statement_timeout', {remainingMs.ToString(System.Globalization.CultureInfo.InvariantCulture)}, true)", token);
            object? result;
            if (id != null) result = await HistoryDetail.ReadAsync(db, owner, id.Value, zone, token, protection);
            else
            {
                var reads = new HistoryReadService(db, new HistoryCursor(protection));
                await reads.PrepareQueryAsync(owner, query!, token);
                result = overview ? await reads.OverviewAsync(owner, query!, token) : await reads.PageAsync(owner, query!, token);
            }
            token.ThrowIfCancellationRequested();
            if (result == null) return ProtectedStatus(404);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(result, jsonOptions.Value.JsonSerializerOptions);
            token.ThrowIfCancellationRequested();
            return File(bytes, "application/json; charset=utf-8");
        }
        catch { failed = true; throw; }
        finally { await EndSnapshotAsync(transaction, failed); }
    }

    /// <summary>A disconnect must not strand a snapshot or replace the primary query/projection failure.</summary>
    private static async Task EndSnapshotAsync(IDbContextTransaction transaction, bool failed)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Exception? error = null;
        try { await transaction.RollbackAsync(cleanup.Token).WaitAsync(cleanup.Token); }
        catch (Exception exception) { error = exception; }
        try { await transaction.DisposeAsync().AsTask().WaitAsync(cleanup.Token); }
        catch (Exception exception) { error ??= exception; }
        if (error != null && !failed) throw new InvalidOperationException("History read cleanup failed.");
    }

    /// <summary>Leave protected empty errors to the existing API middleware instead of MVC ProblemDetails.</summary>
    private IActionResult ProtectedStatus(int status)
    {
        Response.StatusCode = status;
        return new EmptyResult();
    }

    /// <summary>Messages are fixed and contain no private filter, identifier, SQL or credential content.</summary>
    private static string Message(string code) => code switch
    {
        "historyPreparationRequired" => "History preparation is required for this operation.",
        "continuationInvalid" => "Invalid continuation.",
        "activityInvalid" => "Invalid activity selection.",
        "historyOwnerAdmissionExceeded" => "Owner history read capacity exceeded.",
        "historyAdmissionUnavailable" => "History read capacity is temporarily unavailable.",
        _ => "Invalid history parameters."
    };
}
