using Microsoft.EntityFrameworkCore;
using Npgsql;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Util;

/// <summary>Exercises Quartz schema installation through the production PostgreSQL seam.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class QuartzSchemaInstallerPostgresTests(PostgresImportTestFixture fixture)
{
    /// <summary>Proves a fresh schema receives the Quartz 3.19.1 optional-column contract.</summary>
    [PostgresFact]
    public async Task EnsureQuartzTablesExistAsync_FreshSchema_CreatesAlignedColumns()
    {
        await WithSchemaAsync(async (connection, schema) =>
        {
            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(connection, CancellationToken.None);
            await AssertAlignedAsync(connection, schema);
        });
    }

    /// <summary>Proves old persisted rows and legacy correction survive in-place alignment.</summary>
    [PostgresFact]
    public async Task EnsureQuartzTablesExistAsync_OldSchema_PreservesRepresentativeData()
    {
        await WithSchemaAsync(async (connection, schema) =>
        {
            await PrepareOldSchemaAsync(connection);
            await InsertRepresentativeRowsAsync(connection);

            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(connection, CancellationToken.None);

            await AssertAlignedAsync(connection, schema);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT j.job_class_name, encode(j.job_data, 'hex'), t.next_fire_time, t.prev_fire_time,
                       t.trigger_state, encode(t.job_data, 'hex'), t.misfire_orig_fire_time,
                       t.execution_group, t.preferred_node, t.preferred_node_auto,
                       f.fired_time, f.sched_time, f.state, f.execution_group
                FROM qrtz_job_details j
                JOIN qrtz_triggers t USING (sched_name, job_name, job_group)
                JOIN qrtz_fired_triggers f USING (sched_name, trigger_name, trigger_group)
                WHERE j.sched_name = 'Quartz478'
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("Wayfarer.Jobs.LogCleanupJob, Wayfarer", reader.GetString(0));
            Assert.Equal("0001feff", reader.GetString(1));
            Assert.Equal(1700000000100, reader.GetInt64(2));
            Assert.Equal(1699999999000, reader.GetInt64(3));
            Assert.Equal("WAITING", reader.GetString(4));
            Assert.Equal("102030", reader.GetString(5));
            Assert.True(reader.IsDBNull(6));
            Assert.True(reader.IsDBNull(7));
            Assert.True(reader.IsDBNull(8));
            Assert.False(reader.GetBoolean(9));
            Assert.Equal(1700000000000, reader.GetInt64(10));
            Assert.Equal(1700000000100, reader.GetInt64(11));
            Assert.Equal("ACQUIRED", reader.GetString(12));
            Assert.True(reader.IsDBNull(13));
        });
    }

    /// <summary>Proves a compatible partial schema converges and repeated execution is inert.</summary>
    [PostgresFact]
    public async Task EnsureQuartzTablesExistAsync_PartialAndRepeatedExecution_Converges()
    {
        await WithSchemaAsync(async (connection, schema) =>
        {
            await PrepareOldSchemaAsync(connection);
            await ExecuteAsync(connection, "ALTER TABLE qrtz_triggers ADD execution_group VARCHAR(200) NULL");
            await InsertRepresentativeRowsAsync(connection);

            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(connection, CancellationToken.None);
            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(connection, CancellationToken.None);

            await AssertAlignedAsync(connection, schema);
            Assert.Equal(1L, await ScalarInt64Async(connection,
                "SELECT count(*) FROM qrtz_triggers WHERE sched_name = 'Quartz478' AND preferred_node_auto = FALSE"));
        });
    }

    /// <summary>Proves incompatible false-default drift fails and rolls back all alignment work.</summary>
    [PostgresFact]
    public async Task EnsureQuartzTablesExistAsync_IncompatibleDefault_RollsBack()
    {
        await WithSchemaAsync(async (connection, schema) =>
        {
            await PrepareOldSchemaAsync(connection);
            await InsertRepresentativeRowsAsync(connection);
            await ExecuteAsync(connection,
                "ALTER TABLE qrtz_triggers ADD preferred_node_auto BOOL NOT NULL DEFAULT TRUE");

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(connection, CancellationToken.None));

            Assert.Contains("qrtz_triggers.preferred_node_auto", error.Message, StringComparison.Ordinal);
            Assert.Contains("expected BOOL NOT NULL DEFAULT FALSE", error.Message, StringComparison.Ordinal);
            Assert.Contains("default=non-false", error.Message, StringComparison.Ordinal);
            Assert.Equal(1L, await ScalarInt64Async(connection,
                "SELECT count(*) FROM qrtz_triggers WHERE sched_name = 'Quartz478'"));
            Assert.Equal(0L, await ScalarInt64Async(connection,
                "SELECT count(*) FROM information_schema.columns WHERE table_schema = current_schema() " +
                "AND table_name = 'qrtz_triggers' AND column_name = 'misfire_orig_fire_time'"));
        });
    }

    /// <summary>Starts two production installers together and proves they converge without object races.</summary>
    [PostgresFact]
    public async Task EnsureQuartzTablesExistAsync_ConcurrentInstallers_BothComplete()
    {
        await WithSchemaAsync(async (firstConnection, schema) =>
        {
            await using var secondContext = fixture.CreateContext();
            var secondConnection = (NpgsqlConnection)secondContext.Database.GetDbConnection();
            await secondConnection.OpenAsync();
            await ExecuteAsync(secondConnection, $"SET search_path TO {schema}");
            await using var controlContext = fixture.CreateContext();
            var controlConnection = (NpgsqlConnection)controlContext.Database.GetDbConnection();
            await controlConnection.OpenAsync();
            await using var blocker = await controlConnection.BeginTransactionAsync();
            var identity = QuartzSchemaInstaller.AdvisoryLockIdentity;
            await using (var lockCommand = controlConnection.CreateCommand())
            {
                lockCommand.Transaction = blocker;
                lockCommand.CommandText = "SELECT pg_advisory_xact_lock(@namespace, @resource)";
                lockCommand.Parameters.AddWithValue("namespace", identity.Namespace);
                lockCommand.Parameters.AddWithValue("resource", identity.Resource);
                await lockCommand.ExecuteNonQueryAsync();
            }
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var first = Task.Run(async () =>
            {
                await start.Task;
                await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(firstConnection, CancellationToken.None);
            });
            var second = Task.Run(async () =>
            {
                await start.Task;
                await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(secondConnection, CancellationToken.None);
            });
            start.SetResult();

            using var waitLimit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await CountWaitingInstallersAsync(controlConnection, blocker, identity) != 2)
            {
                waitLimit.Token.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            await blocker.CommitAsync();
            await Task.WhenAll(first, second);
            await AssertAlignedAsync(firstConnection, schema);
            Assert.Equal(11L, await ScalarInt64Async(firstConnection,
                "SELECT count(*) FROM information_schema.tables WHERE table_schema = current_schema() " +
                "AND table_type = 'BASE TABLE' AND table_name LIKE 'qrtz_%'"));
        });
    }

    /// <summary>Creates and removes one isolated schema without exposing connection details.</summary>
    private async Task WithSchemaAsync(Func<NpgsqlConnection, string, Task> test)
    {
        fixture.RequireAvailable();
        await using var context = fixture.CreateContext();
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await connection.OpenAsync();
        var schema = $"quartz_478_{Guid.NewGuid():N}";
        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {schema}");
            await ExecuteAsync(connection, $"SET search_path TO {schema}");
            await test(connection, schema);
        }
        finally
        {
            await ExecuteAsync(connection, "SET search_path TO public");
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    /// <summary>Uses the production fresh path, then removes only the five columns to model the prior schema.</summary>
    private static async Task PrepareOldSchemaAsync(NpgsqlConnection connection)
    {
        await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(connection, CancellationToken.None);
        await ExecuteAsync(connection,
            "ALTER TABLE qrtz_triggers DROP misfire_orig_fire_time, DROP execution_group, " +
            "DROP preferred_node, DROP preferred_node_auto; " +
            "ALTER TABLE qrtz_fired_triggers DROP execution_group");
    }

    /// <summary>Seeds compact relational, timing, state, and binary-data preservation evidence.</summary>
    private static async Task InsertRepresentativeRowsAsync(NpgsqlConnection connection)
    {
        await ExecuteAsync(connection,
            """
            INSERT INTO qrtz_job_details
              (sched_name, job_name, job_group, job_class_name, is_durable, is_nonconcurrent,
               is_update_data, requests_recovery, job_data)
            VALUES ('Quartz478', 'job', 'group', 'LogCleanupJob, Wayfarer', TRUE, FALSE, TRUE, TRUE,
                    decode('0001feff', 'hex'));
            INSERT INTO qrtz_triggers
              (sched_name, trigger_name, trigger_group, job_name, job_group, next_fire_time,
               prev_fire_time, priority, trigger_state, trigger_type, start_time, end_time,
               misfire_instr, job_data)
            VALUES ('Quartz478', 'trigger', 'group', 'job', 'group', 1700000000100,
                    1699999999000, 5, 'WAITING', 'SIMPLE', 1699999990000, 1700009990000,
                    2, decode('102030', 'hex'));
            INSERT INTO qrtz_fired_triggers
              (sched_name, entry_id, trigger_name, trigger_group, instance_name, fired_time,
               sched_time, priority, state, job_name, job_group, is_nonconcurrent, requests_recovery)
            VALUES ('Quartz478', 'entry', 'trigger', 'group', 'node', 1700000000000,
                    1700000000100, 5, 'ACQUIRED', 'job', 'group', FALSE, TRUE)
            """);
    }

    /// <summary>Asserts exact catalog-equivalent definitions for the pinned five columns.</summary>
    private static async Task AssertAlignedAsync(NpgsqlConnection connection, string schema)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT table_name, column_name, data_type, character_maximum_length, is_nullable, column_default
            FROM information_schema.columns
            WHERE table_schema = @schema AND (table_name, column_name) IN (
              ('qrtz_triggers', 'misfire_orig_fire_time'), ('qrtz_triggers', 'execution_group'),
              ('qrtz_fired_triggers', 'execution_group'), ('qrtz_triggers', 'preferred_node'),
              ('qrtz_triggers', 'preferred_node_auto'))
            ORDER BY table_name, column_name
            """;
        command.Parameters.AddWithValue("schema", schema);
        await using var reader = await command.ExecuteReaderAsync();
        var definitions = new List<string>();
        while (await reader.ReadAsync())
            definitions.Add(string.Join('|', reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? "" : reader.GetInt32(3), reader.GetString(4),
                reader.IsDBNull(5) ? "" : reader.GetString(5)));
        Assert.Equal(5, definitions.Count);
        Assert.Contains("qrtz_triggers|misfire_orig_fire_time|bigint||YES|", definitions);
        Assert.Contains("qrtz_triggers|execution_group|character varying|200|YES|", definitions);
        Assert.Contains("qrtz_fired_triggers|execution_group|character varying|200|YES|", definitions);
        Assert.Contains("qrtz_triggers|preferred_node|character varying|200|YES|", definitions);
        Assert.Contains(definitions, value => value.StartsWith(
            "qrtz_triggers|preferred_node_auto|boolean||NO|", StringComparison.Ordinal)
            && value.EndsWith("false", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Reads one count without exposing fixture connection details.</summary>
    private static async Task<long> ScalarInt64Async(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Counts installers deterministically queued behind the fixture-owned advisory lock.</summary>
    private static async Task<long> CountWaitingInstallersAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        (int Namespace, int Resource) identity)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND classid = @namespace " +
            "AND objid = @resource AND granted = FALSE";
        command.Parameters.AddWithValue("namespace", identity.Namespace);
        command.Parameters.AddWithValue("resource", identity.Resource);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    /// <summary>Executes fixture-owned SQL without exposing connection details.</summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
