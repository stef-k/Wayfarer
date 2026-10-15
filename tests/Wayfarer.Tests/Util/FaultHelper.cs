using Wayfarer.Util;

namespace Wayfarer.Tests.Util;

/// <summary>A small disposable executable exercises supervisor failures without product backdoors.</summary>
internal sealed class FaultHelper : IDisposable
{
    private readonly string directory;
    internal string PidPath { get; }
    internal ImageHelperSupervisor Supervisor { get; }

    /// <summary>Fixtures remain inside the isolated workspace and assert actual inherited controls.</summary>
    internal FaultHelper(string body)
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("This prototype is Linux only.");
        directory = Path.Combine(Path.GetTempPath(), "process-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "fault-helper");
        PidPath = Path.Combine(directory, "pid");
        File.WriteAllText(path, $"""
            #!/usr/bin/python3
            import os, sys, signal, time, resource
            assert resource.getrlimit(resource.RLIMIT_AS) == (1073741824,1073741824)
            assert resource.getrlimit(resource.RLIMIT_CPU) == (20,25)
            assert resource.getrlimit(resource.RLIMIT_NOFILE) == (128,128)
            assert resource.getrlimit(resource.RLIMIT_CORE) == (0,0)
            with open(r'{PidPath}', 'w') as pid: pid.write(str(os.getpid()))
            {body}

            """);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        Supervisor = new ImageHelperSupervisor(path);
    }

    /// <summary>Wait only for an observable launch, bounded independently of the tested operation.</summary>
    internal async Task<int> WaitForPid()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!File.Exists(PidPath) || new FileInfo(PidPath).Length == 0)
            await Task.Delay(10, deadline.Token);
        return int.Parse(await File.ReadAllTextAsync(PidPath, deadline.Token));
    }

    /// <summary>Remove only the fixture directory owned by this case.</summary>
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
