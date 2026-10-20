using System.Data;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Npgsql;
using Wayfarer.Models;
using Wayfarer.Models.Dtos;
using Wayfarer.Services;

namespace Wayfarer.Areas.Api.Controllers;

public partial class TripsController
{
    /// <summary>
    /// Returns a complete Trip from one read-only snapshot. Public Trips bypass token lookup;
    /// private Trips require their active owner's bearer, independently of cookie identity.
    /// The 30-second budget covers connection acquisition through buffered MVC JSON preparation.
    /// </summary>
    /// <response code="200">The complete authorized Trip, including all supported graph content.</response>
    /// <response code="401">A private Trip requires a valid owner API token.</response>
    /// <response code="404">The Trip does not exist, before any token lookup.</response>
    [HttpGet("{id}")]
    public async Task<IActionResult> GetTrip(Guid id,
        [FromServices] IDbContextFactory<ApplicationDbContext> contexts,
        [FromServices] IOptions<JsonOptions> jsonOptions)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(HttpContext.RequestAborted);
        budget.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var response = await ReadTripSnapshotAsync(id, contexts, jsonOptions.Value, budget.Token);
            budget.Token.ThrowIfCancellationRequested();
            return response;
        }
        catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (budget.IsCancellationRequested) { return FullReadFailure(); }
    }

    /// <summary>Owns graph, identity, projection and cleanup on the same fresh tracked context.</summary>
    private async Task<IActionResult> ReadTripSnapshotAsync(Guid id,
        IDbContextFactory<ApplicationDbContext> contexts, JsonOptions jsonOptions, CancellationToken token)
    {
        ApplicationDbContext? read = null;
        IDbContextTransaction? transaction = null;
        var failed = false;
        try
        {
            read = await contexts.CreateDbContextAsync(token);
            transaction = await read.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            await read.Database.ExecuteSqlRawAsync(
                "SET TRANSACTION READ ONLY; SET LOCAL transaction_timeout = '30s'", token);
            var trip = await read.Trips
                .Include(t => t.Regions).ThenInclude(r => r.Places)
                .Include(t => t.Regions).ThenInclude(r => r.Areas)
                .Include(t => t.Segments).ThenInclude(s => s.FromPlace).ThenInclude(p => p!.Region)
                .Include(t => t.Segments).ThenInclude(s => s.ToPlace).ThenInclude(p => p!.Region)
                .Include(t => t.Segments).ThenInclude(s => s.Waypoints)
                    .ThenInclude(w => w.Place).ThenInclude(p => p.Region)
                .Include(t => t.Tags)
                .AsSplitQuery()
                .FirstOrDefaultAsync(t => t.Id == id, token);
            if (trip is null) return NotFound();
            if (!trip.IsPublic)
            {
                // Binding failures must not be swallowed by the legacy lookup-exception adapter.
                var resolver = IncomingApiTokenResolver.BindReadContext(HttpContext, read);
                var user = await ResolveReadOwnerAsync(resolver, token);
                if (user is null || user.Id != trip.UserId)
                    return Unauthorized("Trip is private or token invalid.");
            }

            var dto = trip.ToFullApiDto(read, token);
            token.ThrowIfCancellationRequested();
            // Exactly one serialization pass, using the same options and UTF-8 media type as MVC.
            // No entity or deferred projection survives the database boundary.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(dto, jsonOptions.JsonSerializerOptions);
            token.ThrowIfCancellationRequested();
            return File(bytes, "application/json; charset=utf-8");
        }
        catch (Exception exception) when (exception is InvalidDataException or JsonException or NotSupportedException or ArgumentException)
        {
            failed = true;
            // Required-content failures never include geometry, private children or credential bytes.
            _logger.LogError("Trip full projection failed with {ExceptionType}", exception.GetType().Name);
            return FullReadFailure();
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            var cleaned = await DisposeReadAsync(read, transaction);
            if (!cleaned && !failed)
                throw new InvalidOperationException("Trip read cleanup failed.");
        }
    }

    /// <summary>Retains ordinary legacy lookup failures as null, while preserving cancellation and admission.</summary>
    private async Task<ApplicationUser?> ResolveReadOwnerAsync(IncomingApiTokenResolver resolver, CancellationToken token)
    {
        try { return await resolver.ResolveAsync(HttpContext, token); }
        catch (ApiTokenAdmissionException) { throw; }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        // PostgreSQL 18 transaction_timeout (25P04) is a server read failure even before timer delivery.
        catch (PostgresException exception) when (exception.SqlState == "25P04") { throw; }
        catch (Exception exception)
        {
            token.ThrowIfCancellationRequested();
            _logger.LogError("Trip token lookup failed with {ExceptionType}", exception.GetType().Name);
            return null;
        }
    }

    /// <summary>Returns the existing bounded whole-read projection error.</summary>
    private ObjectResult FullReadFailure() => StatusCode(StatusCodes.Status500InternalServerError,
        new { success = false, message = "The trip could not be loaded." });

    /// <summary>Ends the snapshot before transfer, independently of a cancelled request and within five seconds.</summary>
    private async Task<bool> DisposeReadAsync(ApplicationDbContext? read, IDbContextTransaction? transaction)
    {
        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var succeeded = true;
        if (transaction is not null)
        {
            try { await transaction.RollbackAsync(cleanup.Token).WaitAsync(cleanup.Token); }
            catch (Exception exception) { RecordFailure(exception); }
            try { await transaction.DisposeAsync().AsTask().WaitAsync(cleanup.Token); }
            catch (Exception exception) { RecordFailure(exception); }
        }
        if (read is not null)
        {
            try { await read.DisposeAsync().AsTask().WaitAsync(cleanup.Token); }
            catch (Exception exception) { RecordFailure(exception); }
        }
        return succeeded;

        // Preserve any primary query/projection/cancellation failure, even if the server closed the session.
        void RecordFailure(Exception exception)
        {
            succeeded = false;
            _logger.LogError("Trip read cleanup failed with {ExceptionType}", exception.GetType().Name);
        }
    }
}
