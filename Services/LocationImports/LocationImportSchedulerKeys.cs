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

    /// <summary>Reads explicit integer/string authority without coercion or missing-value defaults.</summary>
    internal static bool TryReadExecution(IJobDetail job, out int importId, out int epoch)
    {
        var validId = TryReadNumber(job.JobDataMap, "importId", out importId) && importId > 0;
        var validEpoch = TryReadNumber(job.JobDataMap, "epoch", out epoch);
        return validId && validEpoch;
    }

    /// <summary>Checks the stored projection payload against its durable import generation.</summary>
    internal static bool MatchesExecution(IJobDetail? job, int importId, int epoch) =>
        job is not null && job.JobType == typeof(LocationImportJob)
        && TryReadExecution(job, out var projectedId, out var projectedEpoch)
        && projectedId == importId && projectedEpoch == epoch;

    /// <summary>Accepts only nonnegative integers or invariant decimal strings.</summary>
    private static bool TryReadNumber(JobDataMap map, string key, out int value)
    {
        value = -1;
        if (!map.ContainsKey(key)) return false;
        if (map[key] is int number) { value = number; return number >= 0; }
        return map[key] is string text && int.TryParse(text, System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out value) && value >= 0;
    }
}
