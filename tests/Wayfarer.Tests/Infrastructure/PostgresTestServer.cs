using Npgsql;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Validates the connected guarded database against the maintained PostgreSQL major.</summary>
internal static class PostgresTestServer
{
    /// <summary>Maintained local relational major, independent of the production minor-version pin.</summary>
    internal const int RequiredMajor = 18;

    /// <summary>Probes the actual source server before migrations or disposable database creation.</summary>
    internal static async Task ValidateAsync(string connectionString, CancellationToken cancellationToken = default)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database(), current_setting('server_version_num')::integer";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
            throw new PostgresTestServerConfigurationException(
                "The configured guarded PostgreSQL server did not report its identity.");
        if (!string.Equals(reader.GetString(0), "wayfarer_import_tests", StringComparison.Ordinal))
            throw new PostgresTestServerConfigurationException(
                "The connected guarded PostgreSQL database must be exactly wayfarer_import_tests.");
        ValidateVersion(reader.GetValue(1));
    }

    /// <summary>Requires an integer server_version_num for exactly major 18, failing closed otherwise.</summary>
    internal static void ValidateVersion(object? versionNumber)
    {
        if (versionNumber is not int number || number < 100000)
            throw new PostgresTestServerConfigurationException(
                "The configured guarded PostgreSQL server did not report a valid numeric server version.");
        var major = number / 10000;
        if (major != RequiredMajor)
            throw new PostgresTestServerConfigurationException(
                $"WAYFARER_TEST_POSTGRES_CONNECTION must reach PostgreSQL major {RequiredMajor}; the connected server reported major {major}.");
    }
}

/// <summary>Allows guard-owned diagnostics through fixture sanitization without exposing provider details.</summary>
internal sealed class PostgresTestServerConfigurationException(string message) : InvalidOperationException(message);
