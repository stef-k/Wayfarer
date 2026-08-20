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
        fixture.RequireAvailable();
        await using var context = fixture.CreateContext();
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await connection.OpenAsync();
        var schema = $"quartz_478_{Guid.NewGuid():N}";

        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {schema}; SET search_path TO {schema}");

            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(connection, CancellationToken.None);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT table_name, column_name, data_type, character_maximum_length, is_nullable, column_default
                FROM information_schema.columns
                WHERE table_schema = @schema
                  AND (table_name, column_name) IN (
                    ('qrtz_triggers', 'misfire_orig_fire_time'),
                    ('qrtz_triggers', 'execution_group'),
                    ('qrtz_fired_triggers', 'execution_group'),
                    ('qrtz_triggers', 'preferred_node'),
                    ('qrtz_triggers', 'preferred_node_auto'))
                ORDER BY table_name, column_name
                """;
            command.Parameters.AddWithValue("schema", schema);
            await using var reader = await command.ExecuteReaderAsync();
            var definitions = new List<string>();
            while (await reader.ReadAsync())
            {
                definitions.Add(string.Join('|',
                    reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.IsDBNull(3) ? "" : reader.GetInt32(3), reader.GetString(4),
                    reader.IsDBNull(5) ? "" : reader.GetString(5)));
            }

            Assert.Equal(5, definitions.Count);
            Assert.Contains("qrtz_triggers|misfire_orig_fire_time|bigint||YES|", definitions);
            Assert.Contains("qrtz_triggers|execution_group|character varying|200|YES|", definitions);
            Assert.Contains("qrtz_fired_triggers|execution_group|character varying|200|YES|", definitions);
            Assert.Contains("qrtz_triggers|preferred_node|character varying|200|YES|", definitions);
            Assert.Contains(definitions, value =>
                value.StartsWith("qrtz_triggers|preferred_node_auto|boolean||NO|", StringComparison.Ordinal)
                && value.EndsWith("false", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await ExecuteAsync(connection, "SET search_path TO public");
            await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    /// <summary>Executes fixture-owned setup or cleanup SQL without exposing connection details.</summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
