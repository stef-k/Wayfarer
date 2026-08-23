using System.Collections.Specialized;
using Npgsql;
using Quartz;
using Quartz.Impl;
using Wayfarer.Models.LocationEnrichment;
using Wayfarer.Services.LocationEnrichment;
using Wayfarer.Tests.Infrastructure;
using Xunit;

namespace Wayfarer.Tests.Services;

/// <summary>Proves workflow projection survives a real PostgreSQL Quartz restart without replay.</summary>
[Collection(PostgresEnvironmentEvidenceTestCollection.Name)]
public sealed class LocationEnrichmentQuartzPostgresTests(PostgresImportTestFixture fixture)
{
    [PostgresFact(Timeout = 30_000)]
    public async Task StableOneShotTriggerSurvivesRestartAndRetainsDoNothingMisfirePolicy()
    {
        fixture.RequireAvailable();
        await using var admin = fixture.CreateConnection();
        await admin.OpenAsync();
        var schema = $"quartz_507_{Guid.NewGuid():N}";
        await ExecuteAsync(admin, $"CREATE SCHEMA {schema}");
        IScheduler? first = null;
        IScheduler? restarted = null;
        try
        {
            await ExecuteAsync(admin, $"SET search_path TO {schema}");
            await QuartzSchemaInstaller.EnsureQuartzTablesExistAsync(admin, CancellationToken.None);
            var builder = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { SearchPath = schema };
            var schedulerName = $"Wayfarer507-{Guid.NewGuid():N}";
            first = await new StdSchedulerFactory(Properties(builder.ConnectionString, schedulerName)).GetScheduler();
            var workflow = LocationEnrichmentWorkflow.Create("opaque-user", DateTime.UtcNow);
            workflow.Start(DateTime.UtcNow);

            await new LocationEnrichmentScheduler(first).EnsureScheduledAsync(workflow);
            await first.Shutdown(false);
            first = null;

            restarted = await new StdSchedulerFactory(Properties(builder.ConnectionString, schedulerName)).GetScheduler();
            var trigger = await restarted.GetTrigger(LocationEnrichmentScheduler.TriggerKey(
                workflow.SchedulerId, workflow.Epoch));

            Assert.NotNull(trigger);
            Assert.Equal(MisfireInstruction.SimpleTrigger.RescheduleNextWithRemainingCount,
                Assert.IsAssignableFrom<ISimpleTrigger>(trigger).MisfireInstruction);
            Assert.Single(await restarted.GetTriggersOfJob(LocationEnrichmentScheduler.JobKey(workflow.SchedulerId)));
        }
        finally
        {
            if (first is not null) await first.Shutdown(false);
            if (restarted is not null) await restarted.Shutdown(false);
            await ExecuteAsync(admin, "SET search_path TO public");
            await ExecuteAsync(admin, $"DROP SCHEMA IF EXISTS {schema} CASCADE");
        }
    }

    private static NameValueCollection Properties(string connectionString, string schedulerName) => new()
    {
        ["quartz.scheduler.instanceName"] = schedulerName,
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

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }
}
