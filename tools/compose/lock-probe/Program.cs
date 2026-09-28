using WayfarerRecovery;
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

// Qualify the shipped lock without DB access, or the real engine publication boundary in a disposable DB fixture.
try
{
    if (args is ["scheduler-tick" or "scheduler-crash", var timestamp])
    {
        var scheduler = new RecoveryScheduler(WorkerConfiguration.Load("/config/worker.json"))
        {
            // Terminate after actual durable publication, before any retention or success receipt.
            PublicationCommitted = args[0] == "scheduler-crash" ? () => Environment.Exit(137) : null
        };
        await scheduler.TickAsync(DateTimeOffset.Parse(timestamp, System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
        return 0;
    }
    if (args is ["emergency-pending-death" or "emergency-fail" or "emergency-hold-death" or "emergency-archive-death" or "emergency-committed-death", var reservation])
    {
        var engine = new RecoveryEngine(WorkerConfiguration.Load("/config/worker.json"))
        {
            HoldCreating = args[0] == "emergency-pending-death" ? () => Environment.Exit(137) : null,
            HoldCreated = () =>
            {
                if (args[0] == "emergency-fail") throw new IOException("Injected publication failure.");
                if (args[0] == "emergency-hold-death") Environment.Exit(137);
            },
            MemberPublished = member =>
            {
                if (args[0] == "emergency-archive-death" && member.EndsWith(".tar")) Environment.Exit(137);
            },
            PublicationCommitted = args[0] == "emergency-committed-death" ? () => Environment.Exit(137) : null
        };
        await engine.BackupAsync(null, CancellationToken.None, reservation);
        return 0;
    }
    if (args is ["capture-slot", var slot])
    {
        await new RecoveryEngine(WorkerConfiguration.Load("/config/worker.json"))
            .BackupAsync(DateTimeOffset.Parse(slot, System.Globalization.CultureInfo.InvariantCulture), CancellationToken.None);
        return 0;
    }
    using var exclusion = new RecoveryLock(args.Single());
    Console.WriteLine("acquired");
    Console.Out.Flush();
    Console.ReadLine();
    return 0;
}
catch (IOException) { Console.WriteLine("busy-or-unavailable"); return 1; }
