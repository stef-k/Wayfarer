using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Wayfarer.Util;

/// <summary>Owns one fixed helper path, separate bounded transform/validation admission, Linux controls and reaping.</summary>
public sealed class ImageHelperSupervisor
{
    private const int MaximumInputBytes = 200 * 1024 * 1024;
    private const int MaximumOutputBytes = 50 * 1024 * 1024;
    private static readonly SemaphoreSlim Slots = new(4, 4);
    private static readonly SemaphoreSlim ValidationSlots = new(8, 8);
    private static int admissionDisabled;
    private readonly string helperPath;

    /// <summary>The executable path is trusted startup configuration, never request input.</summary>
    public ImageHelperSupervisor(string helperPath)
    {
        if (!Path.IsPathFullyQualified(helperPath))
            throw new ArgumentException("The helper path must be absolute.", nameof(helperPath));
        this.helperPath = helperPath;
    }

    /// <summary>Borrow input on Linux AMD64/ARM64; cancellation completes only after child cleanup.</summary>
    public Task<ImageTransformResult> TransformAsync(
        ReadOnlyMemory<byte> input,
        ImageTransform recipe,
        TimeSpan remainingOriginBudget,
        CancellationToken cancellationToken = default)
        => RunAsync(input, recipe, remainingOriginBudget, false, cancellationToken);

    /// <summary>Fully decode cache/raw bytes outside origin admission; emit only their canonical MIME.</summary>
    public Task<ImageTransformResult> ValidateAsync(ReadOnlyMemory<byte> input,
        int maximumInputBytes = 50 * 1024 * 1024, CancellationToken cancellationToken = default) =>
        RunAsync(input, new ImageTransform(MaximumInputBytes: maximumInputBytes),
            TimeSpan.FromSeconds(30), true, cancellationToken);

    /// <summary>Admit before creating a thread/child; eight validators never queue or consume the four transform slots.</summary>
    private Task<ImageTransformResult> RunAsync(ReadOnlyMemory<byte> input, ImageTransform recipe,
        TimeSpan remainingOriginBudget, bool validation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)
            || !Valid(recipe) || remainingOriginBudget <= TimeSpan.Zero
            || Volatile.Read(ref admissionDisabled) != 0)
            return Task.FromResult(new ImageTransformResult(ImageTransformStatus.Unavailable));
        if (input.Length > recipe.MaximumInputBytes)
            return Task.FromResult(new ImageTransformResult(ImageTransformStatus.TooLarge));
        var slots = validation ? ValidationSlots : Slots;
        if (!slots.Wait(0))
            return Task.FromResult(new ImageTransformResult(ImageTransformStatus.Unavailable));

        var completion = new TaskCompletionSource<ImageTransformResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        // Linux PDEATHSIG follows the launching thread. This dedicated thread stays
        // alive through reap instead of delegating Process.Start to a retiring pool thread.
        try
        {
            var thread = new Thread(() => Supervise(input, recipe,
                TimeSpan.FromSeconds(Math.Min(30, remainingOriginBudget.TotalSeconds)),
                cancellationToken, validation, slots, completion))
            { IsBackground = true, Name = "Wayfarer image supervisor" };
            thread.Start();
        }
        catch
        {
            slots.Release();
            completion.SetResult(new ImageTransformResult(ImageTransformStatus.Unavailable));
        }
        return completion.Task;
    }

    /// <summary>Launch and retain the owning thread until independent cleanup finishes.</summary>
    private void Supervise(ReadOnlyMemory<byte> input, ImageTransform recipe, TimeSpan budget,
        CancellationToken caller, bool validation, SemaphoreSlim slots,
        TaskCompletionSource<ImageTransformResult> completion)
    {
        Process? process = null;
        var started = false;
        var reaped = false;
        ImageTransformResult result = new(ImageTransformStatus.Unavailable);
        try
        {
            process = new Process { StartInfo = StartInfo(recipe, validation) };
            started = process.Start();
            if (started)
            {
                var outcome = CommunicateAsync(process, input, recipe, budget, caller, validation)
                    .GetAwaiter().GetResult();
                result = outcome.Result;
                reaped = outcome.Reaped;
            }
        }
        catch
        {
            if (started)
                reaped = StopAndReapAsync(process!, []).GetAwaiter().GetResult();
        }
        finally
        {
            process?.Dispose();
            if (started && !reaped)
                Interlocked.Exchange(ref admissionDisabled, 1);
            else
                slots.Release();
        }
        if (caller.IsCancellationRequested)
            completion.TrySetCanceled(caller);
        else
            completion.TrySetResult(result);
    }

    /// <summary>Arguments are individual fixed values; prlimit sets controls before exec.</summary>
    private ProcessStartInfo StartInfo(ImageTransform recipe, bool validation)
    {
        var start = new ProcessStartInfo("/usr/bin/prlimit")
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "--as=1073741824:1073741824", "--cpu=20:25",
            "--nofile=128:128", "--core=0:0", "--", helperPath, validation ? "validate" : "transform",
            "--width", Number(recipe.Width), "--height", Number(recipe.Height),
            "--quality", Number(recipe.Quality), "--output-format", recipe.OutputFormat,
            "--max-input-bytes", Number(recipe.MaximumInputBytes),
            "--max-output-bytes", Number(recipe.MaximumOutputBytes) })
            start.ArgumentList.Add(argument);
        start.Environment["WAYFARER_IMAGE_PARENT_PID"] = Number(Environment.ProcessId);
        return start;
    }

    /// <summary>Concurrent pipe ownership; overflow or read faults trigger immediate termination.</summary>
    private static async Task<(ImageTransformResult Result, bool Reaped)> CommunicateAsync(
        Process process, ReadOnlyMemory<byte> input, ImageTransform recipe,
        TimeSpan budget, CancellationToken caller, bool validation)
    {
        var fault = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = CaptureAsync(process.StandardOutput.BaseStream, validation ? 16 : recipe.MaximumOutputBytes, fault);
        var stderr = CaptureAsync(process.StandardError.BaseStream, 4096, fault);
        var stdin = WriteAsync(process.StandardInput.BaseStream, input, fault);
        Task[] pipes = [stdin, stdout, stderr];
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        deadline.CancelAfter(budget);
        var cancelled = Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token);
        var exited = process.WaitForExitAsync();
        var first = await Task.WhenAny(exited, fault.Task, cancelled).ConfigureAwait(false);
        var reaped = await StopAndReapAsync(process, pipes).ConfigureAwait(false);
        var result = new ImageTransformResult(ImageTransformStatus.Unavailable, ProcessId: process.Id);
        if (!reaped || first == cancelled || fault.Task.IsCompleted)
            return (result, reaped);

        var exit = process.ExitCode;
        result = result with { ExitCode = exit };
        if (exit != 0)
            return (result with { Status = exit switch {
                3 => ImageTransformStatus.TooLarge, 4 => ImageTransformStatus.Failed,
                _ => ImageTransformStatus.Unavailable } }, true);
        if (!stdin.Result)
            return (result, true);
        var bytes = stdout.Result;
        var mime = validation ? ValidationMime(bytes) : CanonicalMime(bytes);
        if (mime is null)
            return (result, true);
        return (result with { Status = ImageTransformStatus.Success, Bytes = validation ? null : bytes,
            MimeType = mime }, true);
    }

    /// <summary>Close stdin, terminate when needed and observe all pipes under one cleanup watchdog.</summary>
    private static async Task<bool> StopAndReapAsync(Process process, Task[] pipes)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
            process.StandardInput.BaseStream.Close();
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await process.WaitForExitAsync(watchdog.Token).ConfigureAwait(false);
            try
            {
                await Task.WhenAll(pipes).WaitAsync(watchdog.Token).ConfigureAwait(false);
            }
            catch (IOException) { }
            return pipes.All(task => task.IsCompleted);
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception
            or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Each pipe has a byte cap; diagnostics are retained only for draining, never forwarded.</summary>
    private static async Task<byte[]> CaptureAsync(Stream stream, int maximum, TaskCompletionSource fault)
    {
        try
        {
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read == 0)
                    return output.ToArray();
                if (read > maximum - output.Length)
                    throw new IOException("Image helper pipe exceeded its cap.");
                output.Write(buffer, 0, read);
            }
        }
        catch
        {
            fault.TrySetResult();
            throw;
        }
    }

    /// <summary>A broken input pipe accompanies some early helper failures; classify its exit first.</summary>
    private static async Task<bool> WriteAsync(Stream stream, ReadOnlyMemory<byte> input,
        TaskCompletionSource fault)
    {
        try
        {
            await stream.WriteAsync(input).ConfigureAwait(false);
            await stream.FlushAsync().ConfigureAwait(false);
            return true;
        }
        catch (IOException) { return false; }
        catch
        {
            fault.TrySetResult();
            throw;
        }
        finally { stream.Dispose(); }
    }

    /// <summary>The validator returns one exact fixed token after a complete bounded decode.</summary>
    private static string? ValidationMime(ReadOnlySpan<byte> bytes) => bytes switch
    {
        _ when bytes.SequenceEqual("image/jpeg"u8) => "image/jpeg",
        _ when bytes.SequenceEqual("image/png"u8) => "image/png",
        _ when bytes.SequenceEqual("image/gif"u8) => "image/gif",
        _ when bytes.SequenceEqual("image/webp"u8) => "image/webp",
        _ => null
    };

    /// <summary>Rust owns full derivative decoding; C# additionally requires canonical boundaries.</summary>
    private static string? CanonicalMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 4 && bytes[..3].SequenceEqual(new byte[] { 255, 216, 255 })
            && bytes[^2..].SequenceEqual(new byte[] { 255, 217 }))
            return "image/jpeg";
        if (bytes.Length >= 45 && bytes[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            && bytes.Slice(12, 4).SequenceEqual("IHDR"u8)
            && bytes[^12..].SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 }))
            return "image/png";
        return null;
    }

    /// <summary>Validate trusted arguments before allocation and child admission.</summary>
    private static bool Valid(ImageTransform recipe) =>
        recipe.Width is > 0 and <= 8192 && recipe.Height is > 0 and <= 8192
        && recipe.Quality is > 0 and <= 100 && recipe.OutputFormat is "auto" or "jpeg" or "png"
        && recipe.MaximumInputBytes is > 0 and <= MaximumInputBytes
        && recipe.MaximumOutputBytes is > 0 and <= MaximumOutputBytes;

    /// <summary>Numeric arguments never depend on the supervisor's locale.</summary>
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
