using System.Data;
using System.Data.Common;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

/// <summary>Owns installation and alignment of Wayfarer's PostgreSQL Quartz schema.</summary>
public static class QuartzSchemaInstaller
{
    private const int AdvisoryLockNamespace = 1463898454;
    private const int AdvisoryLockResource = 478;

    /// <summary>Exposes the stable application-owned lock identity to focused relational tests.</summary>
    internal static (int Namespace, int Resource) AdvisoryLockIdentity =>
        (AdvisoryLockNamespace, AdvisoryLockResource);
    private static readonly string[] RequiredTables =
    [
        "qrtz_blob_triggers", "qrtz_calendars", "qrtz_cron_triggers", "qrtz_fired_triggers",
        "qrtz_job_details", "qrtz_locks", "qrtz_paused_trigger_grps", "qrtz_scheduler_state",
        "qrtz_simple_triggers", "qrtz_simprop_triggers", "qrtz_triggers"
    ];
    private static readonly QuartzColumnDefinition[] RequiredColumns =
    [
        new("qrtz_triggers", "misfire_orig_fire_time", "BIGINT NULL", "bigint", null, true, false),
        new("qrtz_triggers", "execution_group", "VARCHAR(200) NULL", "character varying", 200, true, false),
        new("qrtz_fired_triggers", "execution_group", "VARCHAR(200) NULL", "character varying", 200, true, false),
        new("qrtz_triggers", "preferred_node", "VARCHAR(200) NULL", "character varying", 200, true, false),
        new("qrtz_triggers", "preferred_node_auto", "BOOL NOT NULL DEFAULT FALSE", "boolean", null, false, true)
    ];
    private static readonly Dictionary<string, string> JobTypeNameMappings = new()
    {
        ["LogCleanupJob, Wayfarer"] = "Wayfarer.Jobs.LogCleanupJob, Wayfarer",
        ["AuditLogCleanupJob, Wayfarer"] = "Wayfarer.Jobs.AuditLogCleanupJob, Wayfarer",
        ["VisitCleanupJob, Wayfarer"] = "Wayfarer.Jobs.VisitCleanupJob, Wayfarer",
        ["LocationImportJob, Wayfarer"] = "Wayfarer.Jobs.LocationImportJob, Wayfarer"
    };

    /// <summary>Aligns Quartz tables before the scheduler is resolved and initialized.</summary>
    public static async Task EnsureQuartzTablesExistAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        await EnsureQuartzTablesExistAsync(context.Database.GetDbConnection(), cancellationToken);
    }

    /// <summary>Aligns Quartz through an explicit connection so relational tests exercise production code.</summary>
    internal static async Task EnsureQuartzTablesExistAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        var openedHere = connection.State != ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await ExecuteAsync(connection, transaction,
                    "SELECT pg_advisory_xact_lock(@namespace, @resource)", cancellationToken,
                    ("namespace", AdvisoryLockNamespace), ("resource", AdvisoryLockResource));
                var schema = await GetEffectiveSchemaAsync(connection, transaction, cancellationToken);
                await ExecuteAsync(connection, transaction,
                    $"SET LOCAL search_path TO {QuoteIdentifier(schema)}", cancellationToken);
                var tables = await GetQuartzTablesAsync(connection, transaction, schema, cancellationToken);
                if (tables.Count == 0)
                    await CreateFreshSchemaAsync(connection, transaction, cancellationToken);
                else if (tables.Count != RequiredTables.Length)
                    throw new InvalidOperationException(
                        $"Quartz schema '{schema}' is incomplete: found {tables.Count} of {RequiredTables.Length} required tables; {RequiredTables.Length - tables.Count} missing.");
                else
                    await AddMissingColumnsAsync(connection, transaction, schema, cancellationToken);

                await ValidateRequiredColumnsAsync(connection, transaction, schema, cancellationToken);
                await MigrateJobTypeNamesAsync(connection, transaction, schema, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await transaction.RollbackAsync(CancellationToken.None);
                throw;
            }
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    /// <summary>Finds the first usable schema in the connection's PostgreSQL search path.</summary>
    private static async Task<string> GetEffectiveSchemaAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, "SELECT current_schema()");
        return await command.ExecuteScalarAsync(cancellationToken) as string
            ?? throw new InvalidOperationException("Quartz schema installation requires an effective PostgreSQL schema.");
    }

    /// <summary>Returns owned Quartz tables found in the effective schema.</summary>
    private static async Task<HashSet<string>> GetQuartzTablesAsync(
        DbConnection connection, DbTransaction transaction, string schema, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT table_name FROM information_schema.tables " +
            "WHERE table_schema = @schema AND table_type = 'BASE TABLE' AND table_name = ANY(@tables)");
        AddParameter(command, "schema", schema);
        AddParameter(command, "tables", RequiredTables);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var tables = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
        return tables;
    }

    /// <summary>Executes the embedded fresh-install script inside the installer transaction.</summary>
    private static async Task CreateFreshSchemaAsync(
        DbConnection connection, DbTransaction transaction, CancellationToken cancellationToken)
    {
        var ddl = await LoadEmbeddedSqlAsync("tables_postgres.sql", cancellationToken);
        foreach (var sql in ddl.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            await ExecuteAsync(connection, transaction, sql, cancellationToken);
    }

    /// <summary>Adds only absent Quartz 3.19.1 columns.</summary>
    private static async Task AddMissingColumnsAsync(
        DbConnection connection, DbTransaction transaction, string schema, CancellationToken cancellationToken)
    {
        foreach (var definition in RequiredColumns)
            await ExecuteAsync(connection, transaction,
                $"ALTER TABLE {QuoteIdentifier(schema)}.{QuoteIdentifier(definition.Table)} " +
                $"ADD COLUMN IF NOT EXISTS {QuoteIdentifier(definition.Column)} {definition.Sql}", cancellationToken);
    }

    /// <summary>Validates the five pinned definitions from PostgreSQL's catalog before commit.</summary>
    private static async Task ValidateRequiredColumnsAsync(
        DbConnection connection, DbTransaction transaction, string schema, CancellationToken cancellationToken)
    {
        foreach (var expected in RequiredColumns)
        {
            var observed = await ReadColumnAsync(connection, transaction, schema, expected, cancellationToken);
            if (!expected.Matches(observed))
                throw new InvalidOperationException(
                    $"Incompatible Quartz column {expected.Table}.{expected.Column}: expected {expected.Sql}; observed {observed.Describe()}.");
        }
    }

    /// <summary>Reads one required definition without exposing unrelated catalog data.</summary>
    private static async Task<ObservedColumn> ReadColumnAsync(
        DbConnection connection, DbTransaction transaction, string schema,
        QuartzColumnDefinition expected, CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction,
            "SELECT data_type, character_maximum_length, is_nullable, column_default " +
            "FROM information_schema.columns " +
            "WHERE table_schema = @schema AND table_name = @table AND column_name = @column");
        AddParameter(command, "schema", schema);
        AddParameter(command, "table", expected.Table);
        AddParameter(command, "column", expected.Column);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return ObservedColumn.Missing;
        return new ObservedColumn(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetInt32(1),
            reader.GetString(2) == "YES", reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    /// <summary>Corrects legacy job names within the same atomic installation operation.</summary>
    private static async Task MigrateJobTypeNamesAsync(
        DbConnection connection, DbTransaction transaction, string schema, CancellationToken cancellationToken)
    {
        foreach (var mapping in JobTypeNameMappings)
            await ExecuteAsync(connection, transaction,
                $"UPDATE {QuoteIdentifier(schema)}.qrtz_job_details " +
                "SET job_class_name = @newName WHERE job_class_name = @oldName", cancellationToken,
                ("newName", mapping.Value), ("oldName", mapping.Key));
    }

    /// <summary>Creates a transaction-bound command.</summary>
    private static DbCommand CreateCommand(DbConnection connection, DbTransaction transaction, string sql)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    /// <summary>Executes a transaction-bound command.</summary>
    private static async Task ExecuteAsync(DbConnection connection, DbTransaction transaction, string sql,
        CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var parameter in parameters) AddParameter(command, parameter.Name, parameter.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Adds one database parameter.</summary>
    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    /// <summary>Quotes a discovered PostgreSQL identifier.</summary>
    private static string QuoteIdentifier(string identifier) => $"\"{identifier.Replace("\"", "\"\"")}\"";

    /// <summary>Loads the embedded fresh-install SQL.</summary>
    private static async Task<string> LoadEmbeddedSqlAsync(string fileName, CancellationToken cancellationToken)
    {
        var assembly = typeof(QuartzSchemaInstaller).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        if (resourceName is null) throw new FileNotFoundException($"Embedded resource '{fileName}' not found.");
        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new FileNotFoundException($"Embedded resource '{fileName}' not found.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    /// <summary>Describes one pinned Quartz column.</summary>
    private sealed record QuartzColumnDefinition(string Table, string Column, string Sql,
        string DataType, int? Length, bool Nullable, bool RequiresFalseDefault)
    {
        public bool Matches(ObservedColumn observed) => observed.DataType == DataType
            && observed.Length == Length && observed.Nullable == Nullable
            && (!RequiresFalseDefault || observed.HasFalseDefault);
    }

    /// <summary>Contains bounded PostgreSQL catalog facts.</summary>
    private sealed record ObservedColumn(string? DataType, int? Length, bool Nullable, string? Default)
    {
        public static ObservedColumn Missing { get; } = new(null, null, true, null);
        public bool HasFalseDefault
        {
            get
            {
                var value = Default?.Trim().Trim('(', ')')
                    .Replace("::boolean", "", StringComparison.OrdinalIgnoreCase).Trim('\'');
                return string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
            }
        }
        public string Describe() => DataType is null ? "missing" :
            $"type={DataType}, length={(Length?.ToString() ?? "none")}, nullable={Nullable}, " +
            $"default={(Default is null ? "none" : HasFalseDefault ? "false" : "non-false")}";
    }
}
