using System.Runtime.InteropServices;
using WayfarerRecovery;

[assembly: System.Runtime.Versioning.SupportedOSPlatform("linux")]

// Container termination cancels owned work; the engine reaps its database child before task cleanup.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, signal) => { signal.Cancel = true; cancellation.Cancel(); };
using var terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, signal =>
{
    signal.Cancel = true;
    cancellation.Cancel();
});
return await WorkerCli.RunAsync(args, cancellation.Token);
