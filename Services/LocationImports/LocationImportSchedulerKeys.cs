using Quartz;
using Wayfarer.Jobs;

namespace Wayfarer.Services.LocationImports;

/// <summary>Constructs bounded server-owned Quartz identities and string-only payloads.</summary>
public static class LocationImportSchedulerKeys
{
    public const string Group = "Imports";
    public static JobKey Job(int importId, int epoch) => new($"LocationImportJob_{importId}_{epoch}", Group);
    public static TriggerKey Trigger(int importId, int epoch) => new($"LocationImportTrigger_{importId}_{epoch}", Group);

    public static IJobDetail BuildJob(int importId, int epoch) => JobBuilder.Create<LocationImportJob>()
        .WithIdentity(Job(importId, epoch))
        .UsingJobData("importId", importId.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .UsingJobData("epoch", epoch.ToString(System.Globalization.CultureInfo.InvariantCulture))
        .StoreDurably()
        .Build();

    public static ITrigger BuildTrigger(int importId, int epoch) => TriggerBuilder.Create()
        .WithIdentity(Trigger(importId, epoch))
        .ForJob(Job(importId, epoch))
        .StartNow()
        .Build();
}
