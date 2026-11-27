using Quartz;

namespace Wayfarer.Jobs;

/// <summary>Registers receipt maintenance with Quartz's existing scoped job factory and scheduler.</summary>
internal static class ReceiptMaintenance
{
    /// <summary>Creates a fresh database context with each job scope.</summary>
    internal static void Register(IServiceCollection services)
    {
        services.AddTransient<LocationMutationCleanupJob>();
        services.AddTransient<TripMetadataReceiptMaintenanceJob>();
    }

    /// <summary>Preserves each history owner's independent maintenance schedule.</summary>
    internal static void Configure(IScheduler scheduler)
    {
        LocationMutationCleanupJob.Configure(scheduler);
        TripMetadataReceiptMaintenanceJob.Configure(scheduler);
    }
}
