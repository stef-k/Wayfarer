using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using Xunit;

namespace Wayfarer.Tests.Controllers;

public sealed partial class RegionConditionalPostgresTests
{
    /// <summary>Observes actual SQL and identifies the final Region lock independently of the Trip prelock.</summary>
    private sealed class RegionProbe : DbCommandInterceptor
    {
        internal TaskCompletionSource LockStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int ProcessId { get; private set; }
        internal List<string> Updates { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            if (command.CommandText.Contains("\"Regions\"") && command.CommandText.Contains("FOR UPDATE"))
            {
                ProcessId = ((NpgsqlConnection)command.Connection!).ProcessID;
                LockStarted.TrySetResult();
            }
            return ValueTask.FromResult(result);
        }

        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Record(command);
            return ValueTask.FromResult(result);
        }

        /// <summary>Captures property footprints without sensitive parameter values.</summary>
        private void Record(DbCommand command)
        {
            if (command.CommandText.StartsWith("UPDATE ")) Updates.Add(command.CommandText);
        }
    }

    /// <summary>Confirms a PostgreSQL blocker rather than relying on command-start timing.</summary>
    private async Task WaitForBlockAsync(int processId)
    {
        await using var connection = fixture.CreateConnection();
        await connection.OpenAsync();
        await using var query = new NpgsqlCommand("SELECT cardinality(pg_blocking_pids($1)) > 0", connection);
        query.Parameters.AddWithValue(processId);
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if ((bool)(await query.ExecuteScalarAsync())!) return;
            await Task.Delay(20);
        }
        Assert.Fail("The routed request did not reach a PostgreSQL row-lock wait.");
    }
}
