using WayfarerRecovery;
[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

// Qualify the actual shipped primitive/range, independently of database availability.
try
{
    using var exclusion = new RecoveryLock(args.Single());
    Console.WriteLine("acquired");
    Console.Out.Flush();
    Console.ReadLine();
    return 0;
}
catch (IOException) { Console.WriteLine("busy-or-unavailable"); return 1; }
