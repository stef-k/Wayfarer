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
    private const string InitializationFailureMessage = "PostgreSQL migration test database initialization failed.";
    private const string CleanupFailureMessage = "PostgreSQL migration test database cleanup failed.";
    private const string ConnectionVariable = "WAYFARER_TEST_POSTGRES_CONNECTION";
    private const string PersistentDatabase = "wayfarer_import_tests";
    private const string MaintenanceDatabase = "postgres";
    private static readonly HashSet<string> ForbiddenDatabases = new(StringComparer.OrdinalIgnoreCase)
        { PersistentDatabase, "wayfarer", MaintenanceDatabase, "template0", "template1" };
    private readonly IServiceProvider _serviceProvider = new ServiceCollection()
        .AddEntityFrameworkNpgsql()
        .BuildServiceProvider();
    private readonly IPostgresMigrationDatabaseOperations _operations;
    private string? _connectionString;
    private string? _maintenanceConnectionString;
    private string? _ownedDatabase;
    private bool _primaryFailureOccurred;

    /// <summary>Fixed prefix required for every fixture-owned disposable database.</summary>
    public const string DatabasePrefix = "wayfarer_migration_tests_";

    /// <summary>Creates a fixture backed by the real guarded PostgreSQL administrative operations.</summary>
    public PostgresMigrationTestFixture() : this(new NpgsqlMigrationDatabaseOperations()) { }

    /// <summary>Creates a fixture with a narrow test-only administrative seam.</summary>
    internal PostgresMigrationTestFixture(IPostgresMigrationDatabaseOperations operations) => _operations = operations;

    /// <summary>Gets the fixture-owned connection after successful initialization.</summary>
    public string ConnectionString
    {
        get { RequireAvailable(); return _connectionString!; }
    }

    /// <summary>Gets whether the guarded PostgreSQL prerequisite is configured.</summary>
    public bool IsAvailable => _connectionString is not null;

    /// <summary>Creates, provisions, and migrates one empty fixture-owned database.</summary>
    public Task InitializeAsync() => InitializeAsync(CancellationToken.None);

    /// <summary>Creates and migrates the owned database while preserving cancellation semantics.</summary>
    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var value = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(value) && _operations is NpgsqlMigrationDatabaseOperations) return;
        value ??= "Host=fixture.test;Port=5432;Database=wayfarer_import_tests;Username=fixture";

        NpgsqlConnectionStringBuilder source;
        try
        {
            source = new NpgsqlConnectionStringBuilder(value);
            ValidateSource(source);
            await _operations.ValidateServerAsync(source.ConnectionString, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new InvalidOperationException(InitializationFailureMessage); }

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
            await _operations.CreateDatabaseAsync(_maintenanceConnectionString, _ownedDatabase, cancellationToken);
            _connectionString = disposable.ConnectionString;
            await _operations.MigrateAsync(_connectionString, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            _primaryFailureOccurred = true;
            await TryDropAfterPrimaryFailureAsync();
            throw;
        }
        catch (Exception)
        {
            _primaryFailureOccurred = true;
            await TryDropAfterPrimaryFailureAsync();
            throw new InvalidOperationException(InitializationFailureMessage);
        }
    }

    /// <summary>Drops only the exact database generated and retained by this fixture.</summary>
    public async Task DisposeAsync()
    {
        try { await DropOwnedDatabaseAsync(CancellationToken.None); }
        catch (Exception) when (_primaryFailureOccurred) { }
        catch (Exception) { throw new InvalidOperationException(CleanupFailureMessage); }
        finally
        {
            _connectionString = null;
            _maintenanceConnectionString = null;
            _ownedDatabase = null;
            _primaryFailureOccurred = false;
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

    private static void ValidateGeneratedName(string databaseName)
    {
        if (!databaseName.StartsWith(DatabasePrefix, StringComparison.Ordinal)
            || databaseName.Length != DatabasePrefix.Length + 32
            || !Guid.TryParseExact(databaseName[DatabasePrefix.Length..], "N", out _)
            || ForbiddenDatabases.Contains(databaseName))
            throw new InvalidOperationException("Migration fixture database name failed its safety guard.");
    }

    private async Task TryDropAfterPrimaryFailureAsync()
    {
        try { await DropOwnedDatabaseAsync(CancellationToken.None); }
        catch (Exception) { }
    }

    private async Task DropOwnedDatabaseAsync(CancellationToken cancellationToken)
    {
        if (_ownedDatabase is null || _maintenanceConnectionString is null) return;
        var target = _ownedDatabase;
        ValidateCleanupTarget(_ownedDatabase, target);
        await _operations.DropDatabaseAsync(
            _maintenanceConnectionString, target, _connectionString, cancellationToken);
        _connectionString = null;
        _ownedDatabase = null;
    }
}

/// <summary>Defines the narrow administrative lifecycle seam used for deterministic fixture tests.</summary>
internal interface IPostgresMigrationDatabaseOperations
{
    Task ValidateServerAsync(string connectionString, CancellationToken cancellationToken);
    Task CreateDatabaseAsync(string maintenanceConnectionString, string databaseName, CancellationToken cancellationToken);
    Task MigrateAsync(string connectionString, CancellationToken cancellationToken);
    Task DropDatabaseAsync(string maintenanceConnectionString, string databaseName, string? disposableConnectionString, CancellationToken cancellationToken);
}

/// <summary>Performs guarded PostgreSQL administrative work for the disposable fixture.</summary>
internal sealed class NpgsqlMigrationDatabaseOperations : IPostgresMigrationDatabaseOperations
{
    public async Task ValidateServerAsync(string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT current_database(), current_setting('server_version_num')::integer";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        if (!string.Equals(reader.GetString(0), "wayfarer_import_tests", StringComparison.Ordinal) || reader.GetInt32(1) / 10000 != 17)
            throw new InvalidOperationException("Unexpected PostgreSQL test server.");
    }

    public async Task CreateDatabaseAsync(string maintenanceConnectionString, string databaseName, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE {QuoteIdentifier(databaseName)}";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MigrateAsync(string connectionString, CancellationToken cancellationToken)
    {
        var services = new ServiceCollection().AddEntityFrameworkNpgsql().BuildServiceProvider();
        try
        {
            var options = new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseNpgsql(connectionString, provider => provider.UseNetTopologySuite()).Options;
            await using var context = new ApplicationDbContext(options, services);
            await context.Database.MigrateAsync(cancellationToken);
        }
        finally
        {
            if (services is IAsyncDisposable disposable) await disposable.DisposeAsync();
        }
    }

    public async Task DropDatabaseAsync(
        string maintenanceConnectionString, string databaseName, string? disposableConnectionString,
        CancellationToken cancellationToken)
    {
        if (disposableConnectionString is not null)
            NpgsqlConnection.ClearPool(new NpgsqlConnection(disposableConnectionString));
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var terminate = connection.CreateCommand())
        {
            terminate.CommandText = "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = @database AND pid <> pg_backend_pid()";
            terminate.Parameters.AddWithValue("database", databaseName);
            await terminate.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var drop = connection.CreateCommand();
        drop.CommandText = $"DROP DATABASE IF EXISTS {QuoteIdentifier(databaseName)}";
        await drop.ExecuteNonQueryAsync(cancellationToken);
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
