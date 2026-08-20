using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Npgsql;
using Quartz;
using Quartz.Impl;
using Wayfarer.Tests.Infrastructure;
using Xunit;
using QuartzLogContext = Quartz.Logging.LogContext;
using QuartzLogProvider = Quartz.Logging.LogProvider;

namespace Wayfarer.Tests.Util;

/// <summary>Exercises the pinned Quartz lifecycle against the production-aligned PostgreSQL schema.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class QuartzSchemaLifecyclePostgresTests(PostgresImportTestFixture fixture)
{
    private const string MisfireWarning = "Column MISFIRE_ORIG_FIRE_TIME not found in triggers table.";
    private const string PreferredNodeWarning = "Columns PREFERRED_NODE / PREFERRED_NODE_AUTO not found in triggers table.";

    /// <summary>Starts Quartz 3.19.1, performs a persisted operation, and captures unfiltered logs.</summary>
    [PostgresFact]
    public async Task AlignedSchema_QuartzLifecycle_OmitsMissingColumnWarnings()
    {
        fixture.RequireAvailable();
        await using var connection = fixture.CreateConnection();
        var quartzConnectionString = connection.ConnectionString;
        await connection.OpenAsync();
        var schema = $"quartz_478_{Guid.NewGuid():N}";
        var logs = new CapturingLoggerProvider();
        var loggerFactory = LoggerFactory.Create(builder =>
            builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        var loggingWasDisabled = QuartzLogProvider.IsDisabled;
        IScheduler? scheduler = null;
        Exception? failure = null;

        try
        {
            await ExecuteAsync(connection, $"CREATE SCHEMA {schema}");
            await ExecuteAsync(connection, $"SET search_path TO {schema}");
            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(connection, CancellationToken.None);
            var connectionBuilder = new NpgsqlConnectionStringBuilder(quartzConnectionString)
            {
                SearchPath = schema
            };
            QuartzLogContext.SetCurrentLogProvider(loggerFactory);
            var factory = new StdSchedulerFactory(CreateQuartzProperties(connectionBuilder.ConnectionString));
            scheduler = await factory.GetScheduler();

            await scheduler.Start();
            var jobKey = new JobKey("schema-lifecycle", "issue-478");
            await scheduler.AddJob(JobBuilder.Create<LifecycleJob>().WithIdentity(jobKey).StoreDurably().Build(), true);
            Assert.True(await scheduler.CheckExists(jobKey));

            Assert.NotEmpty(logs.Entries);
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(MisfireWarning, StringComparison.Ordinal));
            Assert.DoesNotContain(logs.Entries, entry => entry.Message.Contains(PreferredNodeWarning, StringComparison.Ordinal));
        }
        catch (Exception exception)
        {
            failure = exception;
        }
        finally
        {
            try
            {
                if (scheduler is not null) await scheduler.Shutdown(true);
            }
            catch (Exception exception)
            {
                failure = CombineFailures(failure, exception);
            }

            try
            {
                // Quartz 3.19.1 exposes no provider getter; null resets its supported provider discovery.
                QuartzLogProvider.SetCurrentLogProvider(null!);
            }
            catch (Exception exception)
            {
                failure = CombineFailures(failure, exception);
            }

            try
            {
                QuartzLogProvider.IsDisabled = loggingWasDisabled;
            }
            catch (Exception exception)
            {
                failure = CombineFailures(failure, exception);
            }

            try
            {
                await ExecuteAsync(connection, "SET search_path TO public");
                await ExecuteAsync(connection, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
            }
            catch (Exception exception)
            {
                failure = CombineFailures(failure, exception);
            }

            try
            {
                loggerFactory.Dispose();
            }
            catch (Exception exception)
            {
                failure = CombineFailures(failure, exception);
            }

            try
            {
                logs.Dispose();
            }
            catch (Exception exception)
            {
                failure = CombineFailures(failure, exception);
            }
        }

        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    /// <summary>Keeps the original test failure first while exposing a cleanup failure.</summary>
    private static Exception CombineFailures(Exception? failure, Exception cleanupFailure) => failure is null
        ? cleanupFailure
        : new AggregateException("Quartz lifecycle failed and cleanup also failed.", failure, cleanupFailure);

    /// <summary>Matches Wayfarer's pinned real PostgreSQL scheduler settings without enabling excluded features.</summary>
    private static NameValueCollection CreateQuartzProperties(string connectionString) => new()
    {
        ["quartz.scheduler.instanceName"] = $"Quartz478-{Guid.NewGuid():N}",
        ["quartz.scheduler.instanceId"] = "AUTO",
        ["quartz.jobStore.type"] = "Quartz.Impl.AdoJobStore.JobStoreTX, Quartz",
        ["quartz.jobStore.driverDelegateType"] = "Quartz.Impl.AdoJobStore.PostgreSQLDelegate, Quartz",
        ["quartz.jobStore.tablePrefix"] = "qrtz_",
        ["quartz.jobStore.useProperties"] = "true",
        ["quartz.jobStore.dataSource"] = "default",
        ["quartz.dataSource.default.provider"] = "Npgsql",
        ["quartz.dataSource.default.connectionString"] = connectionString,
        ["quartz.serializer.type"] = "Quartz.Simpl.JsonObjectSerializer, Quartz.Serialization.Json"
    };

    /// <summary>Executes fixture-owned schema setup and cleanup.</summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Minimal persisted job used only to prove a real scheduler database operation.</summary>
    private sealed class LifecycleJob : IJob
    {
        public Task Execute(IJobExecutionContext context) => Task.CompletedTask;
    }

    /// <summary>Captures every enabled Quartz category and level for exact warning assertions.</summary>
    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Entries);
        public void Dispose() { }
    }

    /// <summary>Logger with no category or level filtering.</summary>
    private sealed class CapturingLogger(string category, ConcurrentQueue<LogEntry> entries) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => entries.Enqueue(new(category, logLevel, formatter(state, exception)));
    }

    /// <summary>One captured category, level, and formatted message.</summary>
    private sealed record LogEntry(string Category, LogLevel Level, string Message);
}
