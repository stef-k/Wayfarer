using Xunit;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Proves the guarded server prerequisite and preserves process-wide opt-in configuration.</summary>
[Collection(PostgresPrerequisiteTestCollection.Name)]
public sealed class PostgresTestServerTests
{
    private const string ConnectionVariable = "WAYFARER_TEST_POSTGRES_CONNECTION";

    /// <summary>Accepts the maintained major without pinning its minor version.</summary>
    [Theory]
    [InlineData(180000)]
    [InlineData(180006)]
    [InlineData(180012)]
    public void ValidateVersion_AcceptsPostgres18(int versionNumber) =>
        PostgresTestServer.ValidateVersion(versionNumber);

    /// <summary>Rejects both older and newer unsupported majors with a bounded diagnostic.</summary>
    [Theory]
    [InlineData(170011, 17)]
    [InlineData(190000, 19)]
    public void ValidateVersion_RejectsUnsupportedMajor(int versionNumber, int major)
    {
        var failure = Assert.Throws<PostgresTestServerConfigurationException>(() =>
            PostgresTestServer.ValidateVersion(versionNumber));

        Assert.Contains("PostgreSQL major 18", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"major {major}.", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Fails closed when the numeric query result is missing, malformed, or invalid.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("unavailable")]
    [InlineData("180006")]
    [InlineData(0)]
    [InlineData(-180006)]
    public void ValidateVersion_RejectsInvalidResult(object? versionNumber)
    {
        var failure = Assert.Throws<PostgresTestServerConfigurationException>(() =>
            PostgresTestServer.ValidateVersion(versionNumber));

        Assert.Contains("valid numeric server version", failure.Message, StringComparison.Ordinal);
    }

    /// <summary>Rejects a database-null query result as unavailable version evidence.</summary>
    [Fact]
    public void ValidateVersion_RejectsDatabaseNull() =>
        Assert.Throws<PostgresTestServerConfigurationException>(() => PostgresTestServer.ValidateVersion(DBNull.Value));

    /// <summary>Preserves opt-in skips and unavailable fixtures when no connection is configured.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public async Task MissingConnection_LeavesFixturesUnavailable(string? value)
    {
        var original = Environment.GetEnvironmentVariable(ConnectionVariable);
        try
        {
            Environment.SetEnvironmentVariable(ConnectionVariable, value);
            await using var imports = new PostgresImportTestFixture();
            await using var migrations = new PostgresMigrationTestFixture();

            await imports.InitializeAsync();
            await migrations.InitializeAsync();

            Assert.False(imports.IsAvailable);
            Assert.False(migrations.IsAvailable);
            Assert.NotNull(new PostgresFactAttribute().Skip);
            Assert.NotNull(new PostgresTheoryAttribute().Skip);
        }
        finally { Environment.SetEnvironmentVariable(ConnectionVariable, original); }
    }

    /// <summary>Rejects non-test databases before either fixture can connect or mutate state.</summary>
    [Theory]
    [InlineData("wayfarer")]
    [InlineData("Wayfarer_import_tests")]
    public async Task WrongDatabase_RejectsConfiguredConnection(string database)
    {
        var original = Environment.GetEnvironmentVariable(ConnectionVariable);
        try
        {
            Environment.SetEnvironmentVariable(ConnectionVariable,
                $"Host=fixture.test;Database={database};Username=fixture");
            await using var imports = new PostgresImportTestFixture();
            await using var migrations = new PostgresMigrationTestFixture();

            await Assert.ThrowsAsync<InvalidOperationException>(() => imports.InitializeAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() => migrations.InitializeAsync());

            Assert.False(imports.IsAvailable);
            Assert.False(migrations.IsAvailable);
            Assert.Null(new PostgresFactAttribute().Skip);
            Assert.Null(new PostgresTheoryAttribute().Skip);
        }
        finally { Environment.SetEnvironmentVariable(ConnectionVariable, original); }
    }
}

/// <summary>Prevents prerequisite tests from mutating the opt-in variable during other tests.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresPrerequisiteTestCollection
{
    /// <summary>Stable collection name for process-wide PostgreSQL prerequisite configuration.</summary>
    public const string Name = "PostgreSQL prerequisite configuration";
}
