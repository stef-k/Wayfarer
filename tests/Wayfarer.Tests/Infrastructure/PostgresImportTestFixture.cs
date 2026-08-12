using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wayfarer.Models;
using Xunit;
using Xunit.Sdk;

namespace Wayfarer.Tests.Infrastructure;

/// <summary>Owns the opt-in PostgreSQL data used only by import reconciliation relational tests.</summary>
public sealed class PostgresImportTestFixture : IAsyncLifetime
{
    private const string ConnectionVariable = "WAYFARER_TEST_POSTGRES_CONNECTION";
    private const string RequiredDatabase = "wayfarer_import_tests";
    private readonly HashSet<Guid> _tagIds = [];
    private readonly HashSet<Guid> _tripIds = [];
    private readonly HashSet<Guid> _transportProfileIds = [];
    private readonly HashSet<string> _userIds = [];
    private readonly IServiceProvider _serviceProvider = new ServiceCollection()
        .AddEntityFrameworkNpgsql()
        .BuildServiceProvider();
    private string? _connectionString;

    /// <summary>Gets whether relational tests have an explicitly configured isolated database.</summary>
    public bool IsAvailable => _connectionString is not null;

    /// <summary>Initializes migrations only after proving the connection names the dedicated test database.</summary>
    public async Task InitializeAsync()
    {
        var value = Environment.GetEnvironmentVariable(ConnectionVariable);
        if (string.IsNullOrWhiteSpace(value)) return;

        var builder = new NpgsqlConnectionStringBuilder(value);
        if (!string.Equals(builder.Database, RequiredDatabase, StringComparison.Ordinal))
            throw new InvalidOperationException($"{ConnectionVariable} must name exactly {RequiredDatabase}.");

        _connectionString = builder.ConnectionString;
        await using var context = CreateContext();
        await context.Database.MigrateAsync();
    }

    /// <summary>Deletes only rows whose IDs were registered by this fixture.</summary>
    public async Task DisposeAsync()
    {
        if (!IsAvailable) return;

        await using var context = CreateContext();
        if (_tripIds.Count > 0)
            await context.Trips.Where(trip => _tripIds.Contains(trip.Id)).ExecuteDeleteAsync();
        if (_transportProfileIds.Count > 0)
            await context.Set<TransportProfile>().Where(profile => _transportProfileIds.Contains(profile.Id)).ExecuteDeleteAsync();
        if (_tagIds.Count > 0)
            await context.Tags.Where(tag => _tagIds.Contains(tag.Id)).ExecuteDeleteAsync();
        if (_userIds.Count > 0)
            await context.Users.Where(user => _userIds.Contains(user.Id)).ExecuteDeleteAsync();
    }

    /// <summary>Creates a PostgreSQL context for a test after checking its prerequisite.</summary>
    public ApplicationDbContext CreateContext(params IInterceptor[] interceptors)
    {
        RequireAvailable();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_connectionString!, options => options.UseNetTopologySuite())
            .AddInterceptors(interceptors)
            .Options;
        return new ApplicationDbContext(options, _serviceProvider);
    }

    /// <summary>Creates a specialized test context over the same guarded PostgreSQL database.</summary>
    internal TContext CreateContext<TContext>(
        Func<DbContextOptions<ApplicationDbContext>, IServiceProvider, TContext> factory,
        params IInterceptor[] interceptors)
        where TContext : ApplicationDbContext
    {
        RequireAvailable();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(_connectionString!, provider => provider.UseNetTopologySuite())
            .AddInterceptors(interceptors)
            .Options;
        return factory(options, _serviceProvider);
    }

    /// <summary>Seeds a fixture-owned identity user and records it for targeted cleanup.</summary>
    public async Task<ApplicationUser> CreateUserAsync()
    {
        RequireAvailable();
        var user = new ApplicationUser
        {
            Id = $"import-fixture-{Guid.NewGuid():N}",
            UserName = $"import-fixture-{Guid.NewGuid():N}",
            NormalizedUserName = $"IMPORT-FIXTURE-{Guid.NewGuid():N}",
            DisplayName = "Import fixture",
            IsActive = true
        };
        await using var context = CreateContext();
        context.Users.Add(user);
        await context.SaveChangesAsync();
        _userIds.Add(user.Id);
        return user;
    }

    /// <summary>Registers a tag so cleanup cannot affect data not created by this fixture.</summary>
    public void RegisterTag(Tag tag) => _tagIds.Add(tag.Id);

    /// <summary>Registers a trip so cleanup cannot affect data not created by this fixture.</summary>
    public void RegisterTrip(Guid tripId) => _tripIds.Add(tripId);

    /// <summary>Registers a compatibility profile so cleanup remains fixture-scoped.</summary>
    public void RegisterTransportProfile(Guid profileId) => _transportProfileIds.Add(profileId);

    /// <summary>Raises a visible skipped result when relational prerequisites are not configured.</summary>
    public void RequireAvailable()
    {
        if (!IsAvailable)
            throw SkipException.ForSkip($"Set {ConnectionVariable} to the dedicated {RequiredDatabase} database to run relational import tests.");
    }
}

/// <summary>Skips relational tests when their deliberately opt-in connection is not configured.</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    /// <summary>Creates a fact with an explicit local PostgreSQL prerequisite.</summary>
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYFARER_TEST_POSTGRES_CONNECTION")))
            Skip = "Set WAYFARER_TEST_POSTGRES_CONNECTION to the dedicated wayfarer_import_tests database to run relational import tests.";
    }
}

/// <summary>Skips PostgreSQL theory cases when their deliberately opt-in connection is not configured.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    /// <summary>Creates a theory with an explicit local PostgreSQL prerequisite.</summary>
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WAYFARER_TEST_POSTGRES_CONNECTION")))
            Skip = "Set WAYFARER_TEST_POSTGRES_CONNECTION to the dedicated wayfarer_import_tests database to run relational import tests.";
    }
}

/// <summary>Serializes tests that migrate and clean the shared dedicated import test database.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresImportTestCollection : ICollectionFixture<PostgresImportTestFixture>
{
    /// <summary>Stable collection name for PostgreSQL import tests.</summary>
    public const string Name = "PostgreSQL import reconciliation";
}

/// <summary>Runs provider environment evidence without overlapping fixture-owned provider rows.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PostgresEnvironmentEvidenceTestCollection : ICollectionFixture<PostgresImportTestFixture>
{
    /// <summary>Stable collection name for isolated provider environment evidence.</summary>
    public const string Name = "PostgreSQL environment evidence";
}
