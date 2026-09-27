using System.Security.Cryptography;
using System.Text.Json;

namespace WayfarerRecovery;

/// <summary>One UTC daily slot with bounded catch-up/retry, durable receipts and publication reconciliation.</summary>
public sealed class RecoveryScheduler(WorkerConfiguration config)
{
    /// <summary>Deterministic installation jitter never changes on restart or container replacement.</summary>
    public static DateTimeOffset DueSlot(WorkerConfiguration config, DateTimeOffset now)
    {
        var hash = SHA256.HashData(config.Installation.ToByteArray());
        var jitter = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(hash) % (uint)(config.JitterMinutes + 1);
        var due = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddMinutes(config.DailyMinute + jitter);
        return due > now ? due.AddDays(-1) : due;
    }

    /// <summary>Small bounded polling interval permits container shutdown; policy remains installation-owned.</summary>
    public async Task RunAsync(CancellationToken token)
    {
        if (!config.Enabled) throw new IOException("Scheduler disabled.");
        while (!token.IsCancellationRequested)
        {
            try { await TickAsync(DateTimeOffset.UtcNow, token); }
            catch (IOException) { Console.Error.WriteLine("Recovery scheduler deferred: lock, receipt, source or destination unavailable."); }
            await Task.Delay(TimeSpan.FromSeconds(30), token);
        }
    }

    /// <summary>Recheck receipts and destination publication under the same exclusion used by manual backup.</summary>
    public async Task TickAsync(DateTimeOffset now, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(config.DeadlineSeconds));
        token = deadline.Token;
        using var exclusion = new RecoveryLock("/control/recovery.lock");
        HostRecoveryOperation.Validate(null);
        var state = Load();
        var slot = DueSlot(config, now);
        if (!ShouldAttempt(state, slot, now, config.Attempts)) return;
        var engine = new RecoveryEngine(config);
        state = state.Slot == slot ? state : new SchedulerReceipt(1, slot, 0, false, now, null, state.LastArchive, state.LastSuccess, "none");
        state = state with { Attempts = state.Attempts + 1, LastAttempt = now, NextRetry = now.AddMinutes(5), Failure = "interrupted" };
        Save(state); // Missing destinations and interrupted attempts both consume the bounded retry budget.
        try
        {
            using (var destination = config.OpenDestination())
            {
                var committed = engine.List(destination, token).FirstOrDefault(value => value.ScheduledSlot >= slot);
                if (committed is not null)
                {
                    using var staged = new RecoveryTaskDirectory();
                    var verified = await ArchiveVerifier.VerifyAsync(destination,
                        ArchiveContract.Name(config.Installation, committed.Completed, committed.Archive), staged.Path, config.Source, config.Installation, token);
                    if (!verified.CompatibilitySupported) throw new IOException("Committed slot is incompatible.");
                    Save(state with { Succeeded = true, NextRetry = null, LastArchive = committed.Archive, LastSuccess = committed.Completed, Failure = "none" });
                    return;
                }
            }
            var result = await engine.BackupLockedAsync(slot, token);
            Save(state with { Succeeded = true, NextRetry = null, LastArchive = result.Archive, LastSuccess = result.Completed,
                Failure = result.RetentionSucceeded ? "none" : "retention-failed" });
            Console.WriteLine(JsonSerializer.Serialize(result, ArchiveContract.Json));
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException)
        {
            Save(state with { Failure = "capture-failed" });
            throw;
        }
    }

    /// <summary>Clock rollback and replacement never replay a completed slot or reset consumed attempts.</summary>
    public static bool ShouldAttempt(SchedulerReceipt state, DateTimeOffset slot, DateTimeOffset now, int attempts) =>
        state.Slot < slot || state.Slot == slot && !state.Succeeded && state.Attempts < attempts && !(state.NextRetry > now);

    /// <summary>Corrupt receipts are never reset to a fresh schedule.</summary>
    private static SchedulerReceipt Load()
    {
        const string path = "/control/scheduler.json";
        if (!File.Exists(path)) return new(1, DateTimeOffset.MinValue, 0, false, DateTimeOffset.MinValue, null, null, null, "none");
        using var directory = new SafeDirectory("/control");
        using var input = directory.Read("scheduler.json");
        if (input.Length > 4096) throw new IOException("Scheduler receipt exceeds bound.");
        var state = JsonSerializer.Deserialize<SchedulerReceipt>(input, ArchiveContract.Json);
        if (state is null || state.Schema != 1 || state.Attempts is < 0 or > 3 ||
            state.Failure is not ("none" or "interrupted" or "capture-failed" or "retention-failed") ||
            state.Slot.Offset != TimeSpan.Zero || state.LastAttempt.Offset != TimeSpan.Zero ||
            state.Succeeded && (state.Attempts == 0 || state.NextRetry is not null || state.LastArchive is null || state.LastSuccess is null) ||
            state.LastAttempt < state.Slot || (state.LastArchive is null) != (state.LastSuccess is null))
            throw new IOException("Scheduler receipt invalid.");
        return state;
    }

    /// <summary>Private atomic receipt replacement survives container recreation and does not touch destination state.</summary>
    private static void Save(SchedulerReceipt state)
    {
        var temporary = "/control/.scheduler-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporary, new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                JsonSerializer.Serialize(stream, state, ArchiveContract.Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, "/control/scheduler.json", overwrite: true);
            using var directory = new SafeDirectory("/control");
            directory.Flush();
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

/// <summary>Operational evidence only; no policy, secret or destination authority lives in the receipt.</summary>
public sealed record SchedulerReceipt(int Schema, DateTimeOffset Slot, int Attempts, bool Succeeded,
    DateTimeOffset LastAttempt, DateTimeOffset? NextRetry, Guid? LastArchive, DateTimeOffset? LastSuccess, string Failure);
