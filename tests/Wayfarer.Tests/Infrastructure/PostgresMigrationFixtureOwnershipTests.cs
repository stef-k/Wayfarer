using System.Reflection;
using Wayfarer.Tests.Models;
using Xunit;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Locks fixture ownership and failure-independent cleanup to their intended test seams.</summary>
public sealed class PostgresMigrationFixtureOwnershipTests
{
    private static readonly Type[] OrdinaryClasses =
    [
        typeof(TransportProfilePostgresTests),
        typeof(SegmentWaypointPostgresTests),
        typeof(SegmentMeasurementPostgresTests),
        typeof(UserRoutingConfigurationPostgresTests)
    ];

    private static readonly string[] MigrationClassNames =
    [
        "Wayfarer.Tests.Models.TransportProfileMigrationPostgresTests",
        "Wayfarer.Tests.Models.SegmentWaypointMigrationPostgresTests",
        "Wayfarer.Tests.Models.SegmentMeasurementMigrationPostgresTests",
        "Wayfarer.Tests.Models.UserRoutingConfigurationMigrationPostgresTests"
    ];

    /// <summary>Proves ordinary relational classes remain owned by the persistent fixture.</summary>
    [Fact]
    public void OrdinaryRelationalClasses_UseOnlyPersistentFixture()
    {
        foreach (var type in OrdinaryClasses)
        {
            Assert.DoesNotContain(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()),
                parameter => parameter.ParameterType == typeof(PostgresMigrationTestFixture));
            Assert.Contains(type.GetConstructors().SelectMany(constructor => constructor.GetParameters()),
                parameter => parameter.ParameterType == typeof(PostgresImportTestFixture));
        }
    }

    /// <summary>Proves destructive migration methods live in dedicated disposable-fixture classes.</summary>
    [Fact]
    public void MigrationHistoryMethods_LiveInDedicatedMigrationOnlyClasses()
    {
        foreach (var name in MigrationClassNames)
        {
            var type = typeof(PostgresMigrationFixtureOwnershipTests).Assembly.GetType(name);
            Assert.NotNull(type);
            Assert.Contains(type!.GetConstructors().SelectMany(constructor => constructor.GetParameters()),
                parameter => parameter.ParameterType == typeof(PostgresMigrationTestFixture));
            Assert.All(type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly),
                method => Assert.Contains("Migration", method.Name, StringComparison.Ordinal));
        }
    }

    /// <summary>Proves one fixture construction owns one create and one exact cleanup across assertions.</summary>
    [Fact]
    public async Task SharedLifetime_CreatesOnceAndCleansExactOwnedDatabase()
    {
        var operations = new RecordingMigrationDatabaseOperations();
        await using (var fixture = new PostgresMigrationTestFixture(operations))
        {
            await fixture.InitializeAsync(CancellationToken.None);
            Assert.True(fixture.IsAvailable);
            Assert.NotEmpty(fixture.ConnectionString);
        }

        Assert.Equal(1, operations.CreateCount);
        Assert.Equal([operations.CreatedDatabase], operations.CleanupTargets);
    }

    /// <summary>Proves an explicit clean second lifetime is the only normal second construction.</summary>
    [Fact]
    public async Task SecondLifetime_CreatesOnlyAfterFirstWasDisposed()
    {
        var operations = new RecordingMigrationDatabaseOperations();
        await using (var first = new PostgresMigrationTestFixture(operations))
            await first.InitializeAsync(CancellationToken.None);
        await using (var second = new PostgresMigrationTestFixture(operations))
            await second.InitializeAsync(CancellationToken.None);

        Assert.Equal(2, operations.CreateCount);
        Assert.Equal(2, operations.CleanupTargets.Count);
        Assert.Equal(2, operations.CleanupTargets.Distinct().Count());
    }

    /// <summary>Proves initialization failure is sanitized and exact owned cleanup is attempted.</summary>
    [Fact]
    public async Task InitializationFailure_CleansOwnedDatabaseAndReportsBoundedMessage()
    {
        var operations = new RecordingMigrationDatabaseOperations { MigrationFailure = new("server secret SQL") };
        await using var fixture = new PostgresMigrationTestFixture(operations);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.InitializeAsync(CancellationToken.None));

        Assert.Equal("PostgreSQL migration test database initialization failed.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal([operations.CreatedDatabase], operations.CleanupTargets);
        await fixture.DisposeAsync();
    }

    /// <summary>Proves a cleanup failure cannot replace an earlier initialization failure.</summary>
    [Fact]
    public async Task InitializationAndCleanupFailure_ReportsOnlyBoundedInitializationFailure()
    {
        var operations = new RecordingMigrationDatabaseOperations
        {
            MigrationFailure = new("server secret SQL"),
            CleanupFailure = new("password=secret; DROP DATABASE")
        };
        var fixture = new PostgresMigrationTestFixture(operations);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => fixture.InitializeAsync(CancellationToken.None));

        Assert.Equal("PostgreSQL migration test database initialization failed.", failure.Message);
        Assert.Null(failure.InnerException);
        Assert.Equal("Cleanup also failed.", failure.Data["PostgresMigrationCleanup"]);
        Assert.Equal([operations.CreatedDatabase], operations.CleanupTargets);
        await fixture.DisposeAsync();
    }

    /// <summary>Proves the final create seam rejects an invalid disposable name before database access.</summary>
    [Fact]
    public async Task CreateDatabaseAsync_RejectsInvalidDisposableNameBeforeMutation()
    {
        var operations = new NpgsqlMigrationDatabaseOperations();
        const string source = "Host=fixture.test;Port=5432;Database=wayfarer_import_tests;Username=fixture";
        const string maintenance = "Host=fixture.test;Port=5432;Database=postgres;Username=fixture";
        const string invalid = "wayfarer_import_tests";

        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.CreateDatabaseAsync(
            source, maintenance, invalid, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.DropDatabaseAsync(
            source, maintenance, invalid,
            $"Host=fixture.test;Port=5432;Database={invalid};Username=fixture", CancellationToken.None));
    }

    /// <summary>Proves the final create seam rejects a non-maintenance database before database access.</summary>
    [Fact]
    public async Task CreateDatabaseAsync_RejectsInvalidMaintenanceDatabaseBeforeMutation()
    {
        var operations = new NpgsqlMigrationDatabaseOperations();
        var owned = $"{PostgresMigrationTestFixture.DatabasePrefix}{Guid.NewGuid():N}";
        const string source = "Host=fixture.test;Port=5432;Database=wayfarer_import_tests;Username=fixture";
        const string maintenance = "Host=fixture.test;Port=5432;Database=wayfarer_import_tests;Username=fixture";
        var disposable = $"Host=fixture.test;Port=5432;Database={owned};Username=fixture";

        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.CreateDatabaseAsync(
            source, maintenance, owned, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.DropDatabaseAsync(
            source, maintenance, owned, disposable, CancellationToken.None));
    }

    /// <summary>Proves altered maintenance endpoints fail before create or cleanup database access.</summary>
    [Theory]
    [InlineData("other.test", 5432)]
    [InlineData("fixture.test", 5433)]
    public async Task DatabaseMutations_RejectAlteredMaintenanceEndpointBeforeMutation(string host, int port)
    {
        var operations = new NpgsqlMigrationDatabaseOperations();
        var owned = $"{PostgresMigrationTestFixture.DatabasePrefix}{Guid.NewGuid():N}";
        const string source = "Host=fixture.test;Port=5432;Database=wayfarer_import_tests;Username=fixture";
        var maintenance = $"Host={host};Port={port};Database=postgres;Username=fixture";
        var disposable = $"Host=fixture.test;Port=5432;Database={owned};Username=fixture";

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operations.CreateDatabaseAsync(source, maintenance, owned, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            operations.DropDatabaseAsync(source, maintenance, owned, disposable, CancellationToken.None));
    }

    /// <summary>Proves cleanup rejects a disposable connection that does not identify the owned database.</summary>
    [Fact]
    public async Task DropDatabaseAsync_RejectsAlteredOwnedDatabaseBeforeMutation()
    {
        var operations = new NpgsqlMigrationDatabaseOperations();
        var owned = $"{PostgresMigrationTestFixture.DatabasePrefix}{Guid.NewGuid():N}";
        var altered = $"{PostgresMigrationTestFixture.DatabasePrefix}{Guid.NewGuid():N}";

        await Assert.ThrowsAsync<InvalidOperationException>(() => operations.DropDatabaseAsync(
            "Host=fixture.test;Port=5432;Database=wayfarer_import_tests;Username=fixture",
            "Host=fixture.test;Port=5432;Database=postgres;Username=fixture", owned,
            $"Host=fixture.test;Port=5432;Database={altered};Username=fixture", CancellationToken.None));
    }

    /// <summary>Proves cancellation remains primary while cleanup uses a non-cancelled path.</summary>
    [Fact]
    public async Task CancellationAfterOwnership_CleansOwnedDatabaseAndRemainsCancellation()
    {
        var operations = new RecordingMigrationDatabaseOperations
        {
            MigrationFailure = new OperationCanceledException("raw cancellation"),
            CleanupFailure = new("password=secret; DROP DATABASE")
        };
        await using var fixture = new PostgresMigrationTestFixture(operations);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => fixture.InitializeAsync(new CancellationToken(canceled: true)));

        Assert.DoesNotContain("raw cancellation", failure.Message, StringComparison.Ordinal);
        Assert.Null(failure.InnerException);
        Assert.Equal("Cleanup also failed.", failure.Data["PostgresMigrationCleanup"]);
        Assert.Equal([operations.CreatedDatabase], operations.CleanupTargets);
        Assert.All(operations.CleanupTokens, token => Assert.False(token.IsCancellationRequested));
    }

    /// <summary>Proves cleanup failures are bounded and never expose provider diagnostics.</summary>
    [Fact]
    public async Task CleanupFailure_ReportsBoundedMessageWithoutRawDetails()
    {
        var operations = new RecordingMigrationDatabaseOperations { CleanupFailure = new("password=secret; DROP DATABASE") };
        var fixture = new PostgresMigrationTestFixture(operations);
        await fixture.InitializeAsync(CancellationToken.None);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.DisposeAsync());

        Assert.Equal("PostgreSQL migration test database cleanup failed.", failure.Message);
        Assert.Null(failure.InnerException);
    }

    private sealed class RecordingMigrationDatabaseOperations : IPostgresMigrationDatabaseOperations
    {
        public int CreateCount { get; private set; }
        public string CreatedDatabase { get; private set; } = string.Empty;
        public List<string> CleanupTargets { get; } = [];
        public List<CancellationToken> CleanupTokens { get; } = [];
        public Exception? MigrationFailure { get; init; }
        public Exception? CleanupFailure { get; init; }

        public Task ValidateServerAsync(string connectionString, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task CreateDatabaseAsync(string sourceConnectionString, string maintenanceConnectionString,
            string databaseName, CancellationToken cancellationToken)
        {
            CreateCount++;
            CreatedDatabase = databaseName;
            return Task.CompletedTask;
        }

        public Task MigrateAsync(string connectionString, CancellationToken cancellationToken) =>
            MigrationFailure is null ? Task.CompletedTask : Task.FromException(MigrationFailure);

        public Task DropDatabaseAsync(
            string sourceConnectionString, string maintenanceConnectionString, string databaseName,
            string disposableConnectionString, CancellationToken cancellationToken)
        {
            CleanupTargets.Add(databaseName);
            CleanupTokens.Add(cancellationToken);
            return CleanupFailure is null ? Task.CompletedTask : Task.FromException(CleanupFailure);
        }
    }
}
