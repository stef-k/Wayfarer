using System.Diagnostics;

namespace WayfarerCtl;

/// <summary>Captured child results are private until a command selects safe output.</summary>
public sealed record ProcessResult(int Code, string Output);

/// <summary>The only external execution boundary, replaceable in behavioral tests.</summary>
public interface IProcessRunner
{
    /// <summary>Runs an argument array with optional protected stdin and cancellation.</summary>
    Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null);
}

/// <summary>Invokes Docker directly, drains diagnostics privately, and bounds execution.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Clears ambient Compose interpolation/selection and never forwards child stderr.</summary>
    public async Task<ProcessResult> RunAsync(string[] args, string? input, CancellationToken cancellation, Action<string>? lineOutput = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        if (!args.Contains("--follow")) timeout.CancelAfter(TimeSpan.FromMinutes(10));
        var start = new ProcessStartInfo("docker")
        {
            UseShellExecute = false, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        foreach (var key in start.Environment.Keys.ToArray())
            if (key.StartsWith("COMPOSE_", StringComparison.Ordinal) || Deployment.EnvironmentKeys.Contains(key))
                start.Environment.Remove(key);
        // Restrict management to the local daemon whose host files and sockets preflight inspected.
        start.Environment.Remove("DOCKER_HOST");
        start.Environment.Remove("DOCKER_CONTEXT");
        start.ArgumentList.Insert(0, "unix:///var/run/docker.sock");
        start.ArgumentList.Insert(0, "--host");
        using var process = new Process { StartInfo = start };
        process.Start();
        using var registration = timeout.Token.Register(() =>
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
        });
        var output = ReadBoundedAsync(process.StandardOutput, timeout.Token, lineOutput);
        var error = ReadBoundedAsync(process.StandardError, timeout.Token);
        if (input is not null) await process.StandardInput.WriteLineAsync(input.AsMemory(), timeout.Token);
        process.StandardInput.Close();
        await process.WaitForExitAsync(timeout.Token);
        await error;
        return new ProcessResult(process.ExitCode, await output);
    }

    /// <summary>Drain both pipes without retaining unbounded process or log output.</summary>
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken cancellation, Action<string>? lineOutput = null)
    {
        var result = new System.Text.StringBuilder();
        if (lineOutput is not null)
        {
            // Follow is streamed line-by-line through the command's redaction boundary.
            while (await reader.ReadLineAsync(cancellation) is { } line) lineOutput(line);
            return "";
        }
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, cancellation)) > 0)
        {
            result.Append(buffer, 0, count);
            if (result.Length > 262144) result.Remove(0, result.Length - 262144);
        }
        return result.ToString();
    }
}
