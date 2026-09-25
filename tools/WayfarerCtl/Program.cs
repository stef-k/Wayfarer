using WayfarerCtl;

// Cancellation reaches every child process; no command receives shell-expanded operator input.
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
return await new Cli(new ProcessRunner(), new Terminal()).RunAsync(args, cancellation.Token);
