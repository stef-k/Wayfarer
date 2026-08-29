using System.Runtime.ExceptionServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wayfarer.Models;
using Xunit;
using Xunit.Sdk;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Owns one disposable PostgreSQL database for destructive migration-history tests.</summary>
public sealed class PostgresMigrationTestFixture : IAsyncLifetime
{
    private const string ConnectionVariable = "WAYFARER_TEST_POSTGRES_CONNECTION";
    private const string PersistentDatabase = "wayfarer_import_tests";
    private const string MaintenanceDatabase = "postgres";
    private static readonly HashSet<string> ForbiddenDatabases = new(StringComparer.OrdinalIgnoreCase)
        { PersistentDatabase, "wayfarer", MaintenanceDatabase, "template0", "template1" };
    private readonly IServiceProvider _serviceProvider = new ServiceCollection()
        .AddEntityFrameworkNpgsql()
        .BuildServiceProvider();
    private string? _connectionString;
    private string? _maintenanceConnectionString;
    private string? _ownedDatabase;

    /// <summary>Fixed prefix required for every fixture-owned disposable database.</summary>
    public const string DatabasePrefix = "wayfarer_migration_tests_";

    /// <summary>Gets the fixture-owned connection after successful initialization.</summary>
    public string ConnectionString
    {
        get { RequireAvailable(); return _connectionString!; }
    }

    /// <summary>Gets whether the guarded PostgreSQL prerequisite is configured.</summary>
    public bool IsAvailable => _connectionString is not null;

    /// <summary>Creates, provisions, and migrates one empty fixture-owned database.</summary>
    public async Task InitializeAsync()
    {
        var value = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(value)) return;

        var source = new NpgsqlConnectionStringBuilder(value);
        ValidateSource(source);
        await ValidateServerAsync(source.ConnectionString);

        _ownedDatabase = $"{DatabasePrefix}{Guid.NewGuid():N}";
        ValidateGeneratedName(_ownedDatabase);
        var maintenance = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            Database = MaintenanceDatabase,
            Pooling = false
        };
        _maintenanceConnectionString = maintenance.ConnectionString;
        var disposable = new NpgsqlConnectionStringBuilder(source.ConnectionString)
        {
            Database = _ownedDatabase,
            Pooling = false
        };

        try
        {
            await CreateDatabaseAsync(_ownedDatabase);
            _connectionString = disposable.ConnectionString;
            await using var context = CreateContext();
            await context.Database.MigrateAsync();
        }
        catch (Exception primary)
        {
            try { await DropOwnedDatabaseAsync(); }
            catch (Exception cleanup) { primary.Data["MigrationFixtureCleanup"] = cleanup.Message; }
            ExceptionDispatchInfo.Capture(primary).Throw();
        }
    }

    /// <summary>Drops only the exact database generated and retained by this fixture.</summary>
    public async Task DisposeAsync()
    {
        try { await DropOwnedDatabaseAsync(); }
        finally
        {
            _connectionString = null;
            _maintenanceConnectionString = null;
            _ownedDatabase = null;
            if (_serviceProvider is IAsyncDisposable disposable)
                await disposable.DisposeAsync();
        }
    }

    /// <summary>Creates a production context connected only to the fixture-owned database.</summary>
    public ApplicationDbContext CreateContext(params IInterceptor[] interceptors)
    {
        RequireAvailable();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_connectionString!, provider => provider.UseNetTopologySuite())
            .AddInterceptors(interceptors)
            .Options;
        return new ApplicationDbContext(options, _serviceProvider);
    }

    /// <summary>Seeds an identity user inside the disposable database for migration scenarios.</summary>
    public async Task<ApplicationUser> CreateUserAsync()
    {
        RequireAvailable();
        var id = $"migration-fixture-{Guid.NewGuid():N}";
        var user = new ApplicationUser
        {
            Id = id,
            UserName = id,
            NormalizedUserName = id.ToUpperInvariant(),
            DisplayName = "Migration fixture",
            IsActive = true
        };
        await using var context = CreateContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();
        return user;
    }

    /// <summary>Raises a skipped result when the guarded PostgreSQL prerequisite is absent.</summary>
    public void RequireAvailable()
    {
        if (!IsAvailable)
            throw SkipException.ForSkip($"Set {ConnectionVariable} to the dedicated {PersistentDatabase} database to run migration tests.");
    }

    /// <summary>Fails closed unless cleanup targets the exact valid fixture-owned name.</summary>
    internal static void ValidateCleanupTarget(string ownedDatabase, string targetDatabase)
    {
        ValidateGeneratedName(ownedDatabase);
        ValidateGeneratedName(targetDatabase);
        if (!string.Equals(ownedDatabase, targetDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException("Migration fixture cleanup target is not owned by this fixture.");
    }

    private static void ValidateSource(NpgsqlConnectionStringBuilder source)
    {
        if (!string.Equals(source.Database, PersistentDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException($"{ConnectionVariable} must name exactly {PersistentDatabase}.");
        if (string.IsNullOrWhiteSpace(source.Host) || source.Port <= 0)
            throw new InvalidOperationException("The PostgreSQL test connection must identify an expected server and port.");
    }

    private static async Task ValidateServerAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database(), current_setting('server_version_num')::integer";
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        if (!string.Equals(reader.GetString(0), PersistentDatabase, StringComparison.Ordinal) || reader.GetInt32(1) / 10000 != 17)
            throw new InvalidOperationException("The PostgreSQL connection is not the expected guarded test server.");
    }

    private static void ValidateGeneratedName(string databaseName)
    {
        if (!databaseName.StartsWith(DatabasePrefix, StringComparison.Ordinal)
            || databaseName.Length != DatabasePrefix.Length + 32
            || !Guid.TryParseExact(databaseName[DatabasePrefix.Length..], "N", out _)
            || ForbiddenDatabases.Contains(databaseName))
            throw new InvalidOperationException("Migration fixture database name failed its safety guard.");
    }

    private async Task CreateDatabaseAsync(string databaseName)
    {
        ValidateGeneratedName(databaseName);
        await using var connection = new NpgsqlConnection(_maintenanceConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)}";
        await command.ExecuteNonQueryAsync();
    }

    private async Task DropOwnedDatabaseAsync()
    {
        if (_ownedDatabase is null || _maintenanceConnectionString is null) return;
        var target = _ownedDatabase;
        ValidateCleanupTarget(_ownedDatabase, target);
        if (_connectionString is not null)
            NpgsqlConnection.ClearPool(new NpgsqlConnection(_connectionString));

        await using var connection = new NpgsqlConnection(_maintenanceConnectionString);
        await connection.OpenAsync();
        await using (var terminate = connection.CreateCommand())
        {
            terminate.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid()";
            terminate.Parameters.AddWithValue("database", target);
            await terminate.ExecuteNonQueryAsync();
        }
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(target)}";
        await drop.ExecuteNonQueryAsync();
    }

    private static string QuoteIdentifier(string databaseName) => new NpgsqlCommandBuilder().QuoteIdentifier(databaseName);
}

/// <summary>Serializes tests sharing one disposable migration-history database.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresMigrationTestCollection : ICollectionFixture<PostgresMigrationTestFixture>
{
    /// <summary>Stable collection name for destructive migration-history tests.</summary>
    public const string Name = "PostgreSQL migration history";
}
