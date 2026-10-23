using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;
using Wayfarer.Services;

namespace Wayfarer.CommandLine;

/// <summary>Offline history preparation exits before migrations, seeding, jobs, providers or web startup.</summary>
internal static class LocationHistoryCli
{
    /// <summary>Runs status, resumable prepare, or an explicit independent-generation rebuild.</summary>
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length is < 2 or > 3 || args[1] is not ("status" or "prepare" or "rebuild")
            || args.Length == 3 && args[2] is not ("time" or "text" or "both"))
        {
            await error.WriteLineAsync("Usage: history-data status|prepare|rebuild [time|text|both]");
            return 2;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, value) => { value.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        try
        {
            var builder = WebApplication.CreateBuilder(Array.Empty<string>());
            builder.Logging.ClearProviders();
            DatabaseSecret.Apply(builder.Configuration);
            var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(
                builder.Configuration.GetConnectionString("DefaultConnection"), postgres => postgres.UseNetTopologySuite()).Options;
            using var services = builder.Services.BuildServiceProvider();
            await using var db = new ApplicationDbContext(options, services);
            var preparation = new LocationHistoryPreparation(db);
            string[] kinds = args.Length == 3 && args[2] != "both" ? [args[2]] : ["time", "text"];
            // Mark every selected generation unavailable before rebuilding any rows.
            if (args[1] != "status")
                foreach (var kind in kinds) await preparation.BeginAsync(kind, args[1] == "rebuild", cancellation.Token);
            foreach (var kind in kinds)
            {
                if (args[1] != "status")
                {
                    while (!await preparation.PrepareBatchAsync(kind, cancellation.Token))
                        await output.WriteLineAsync(JsonSerializer.Serialize(await preparation.StatusAsync(kind, cancellation.Token)));
                    await db.Database.ExecuteSqlRawAsync("ANALYZE \"Locations\"", cancellation.Token);
                }
            }
            // A CLI status is one coherent database view, suitable for operator readiness decisions.
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellation.Token);
            var ready = true;
            foreach (var kind in kinds)
            {
                var status = await preparation.StatusAsync(kind, cancellation.Token);
                await output.WriteLineAsync(JsonSerializer.Serialize(status));
                ready &= status.Ready;
            }
            await transaction.CommitAsync(cancellation.Token);
            return ready ? 0 : 1;
        }
        catch (OperationCanceledException)
        { await error.WriteLineAsync("History preparation cancelled; committed batches remain resumable."); return 130; }
        catch (Exception)
        { await error.WriteLineAsync("History command failed; verify schema, extension/index privileges, database and pinned rules. Resume prepare or explicitly rebuild changed rules."); return 1; }
        finally { Console.CancelKeyPress -= cancel; }
    }
}
