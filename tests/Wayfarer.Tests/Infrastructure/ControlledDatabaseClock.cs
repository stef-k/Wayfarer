using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using NpgsqlTypes;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Controlled clock at the existing SQL interceptor seam; production still always reads PostgreSQL's real clock.</summary>
internal sealed class ControlledDatabaseClock : DbCommandInterceptor
{
    internal DateTime Now { get; set; } = new(DateTime.UtcNow.Ticks / 10 * 10, DateTimeKind.Utc);

    /// <summary>Parameterizes only explicit clock_timestamp calls, preserving query/lock/transaction execution.</summary>
    private void Apply(DbCommand command)
    {
        if (!command.CommandText.Contains("clock_timestamp()", StringComparison.Ordinal)) return;
        command.CommandText = command.CommandText.Replace("clock_timestamp()", "@controlled_mutation_clock::timestamptz", StringComparison.Ordinal);
        command.Parameters.Add(new NpgsqlParameter("controlled_mutation_clock", NpgsqlDbType.TimestampTz) { Value = Now });
    }

    /// <summary>Reader paths cover issuance, final deadline checks and eligibility reads.</summary>
    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
        InterceptionResult<DbDataReader> result, CancellationToken token = default)
    { Apply(command); return ValueTask.FromResult(result); }

    /// <summary>Cleanup's final SQL eligibility check uses the same controlled clock.</summary>
    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
        InterceptionResult<int> result, CancellationToken token = default)
    { Apply(command); return ValueTask.FromResult(result); }
}
