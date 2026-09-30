using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace WayfarerRecovery;

/// <summary>Private versioned worker protocol; the host operator remains the administrator-facing command owner.</summary>
public static class WorkerCli
{
    [DllImport("libc")]
    private static extern uint umask(uint mask);

    /// <summary>Only fixed operations are accepted; failures expose categories, never captured data or child diagnostics.</summary>
    public static async Task<int> RunAsync(string[] arguments, CancellationToken token)
    {
        try { _ = NativePlatform.Current; }
        catch (PlatformNotSupportedException) { return 2; }
        umask(0x3f);
        try
        {
            if (arguments is ["runtime-check"])
            {
                await RuntimeCheckAsync(token);
                return 0;
            }
            if (arguments.Length > 0 && arguments[0].StartsWith("restore-", StringComparison.Ordinal))
                return await RestoreWorker.RunAsync(arguments, token);
            string? hostOperation = null;
            if (arguments.Length >= 3 && arguments[1] == "--host-operation")
            {
                hostOperation = arguments[2];
                arguments = [arguments[0], .. arguments[3..]];
            }
            if (arguments is ["schedule"])
            {
                await new RecoveryScheduler(WorkerConfiguration.Load("/config/worker.json")).RunAsync(token);
                return 0;
            }
            if (arguments is ["destination-check"])
            {
                CheckDestination();
                Write(new { Schema = 1, Destination = "ready" });
                return 0;
            }
            if (arguments is not (["backup"] or ["backups"] or ["verify"] or ["verify", _])) return 2;
            var config = WorkerConfiguration.Load("/config/worker.json");
            var engine = new RecoveryEngine(config);
            if (arguments[0] == "backup")
            {
                var result = await engine.BackupAsync(null, token, hostOperation);
                Write(result);
                return result.RetentionSucceeded ? 0 : 1;
            }
            using var exclusion = new RecoveryLock("/control/recovery.lock");
            HostRecoveryOperation.Validate(hostOperation);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(config.DeadlineSeconds));
            using var destination = config.OpenDestination();
            var manifests = engine.List(destination, deadline.Token);
            if (arguments[0] == "backups")
            {
                Write(new { Schema = 1, Verification = "not-fully-verified", More = manifests.Length > 20,
                    Archives = manifests.Take(20).Select(value => new
                    {
                        value.Archive, value.Completed, value.Mode, value.Source.ApplicationVersion,
                        SchemaIdentity = value.Database.TerminalMigration,
                        Name = ArchiveContract.Name(value.Installation, value.Completed, value.Archive)
                    }) });
                return 0;
            }
            var name = arguments.Length == 2 ? arguments[1] : manifests.Length > 0
                ? ArchiveContract.Name(config.Installation, manifests[0].Completed, manifests[0].Archive)
                : throw new IOException("No complete owned archive.");
            using var staging = new RecoveryTaskDirectory();
            try
            {
                var verified = await ArchiveVerifier.VerifyAsync(destination, name, staging.Path, config.Source, config.Installation, deadline.Token);
                Write(verified);
                return verified.CompatibilitySupported ? 0 : 1;
            }
            catch (Exception error) when (error is IOException or JsonException or CryptographicException or ArgumentException)
            {
                Write(new { Schema = 1, IntegrityValid = false, CompatibilitySupported = false, Failure = "archive-invalid" });
                return 1;
            }
        }
        catch (OperationCanceledException) { Write(new { Schema = 1, Success = false, Failure = "cancelled-or-deadline" }); return 1; }
        catch (Exception) { Write(new { Schema = 1, Success = false, Failure = "recovery-validation-or-operation-failed" }); return 1; }
    }

    /// <summary>Probe only private destination-owned names; preserve a primary failure if cleanup also fails.</summary>
    private static void CheckDestination()
    {
        var configuration = WorkerConfiguration.Load("/config/worker.json");
        using var probeDestination = configuration.OpenDestination();
        var probeName = ".wayfarer-probe-" + configuration.Installation.ToString("D") + "-" + Guid.NewGuid().ToString("N");
        var owned = probeName;
        var completed = false;
        try
        {
            using (var output = probeDestination.Write(probeName)) { output.Write("probe"u8); output.Flush(true); }
            probeDestination.Publish(probeName, probeName + ".committed");
            owned = probeName + ".committed";
            using var input = probeDestination.Read(owned);
            if (new StreamReader(input).ReadToEnd() != "probe") throw new IOException("Destination readback failed.");
            completed = true;
        }
        finally
        {
            try { probeDestination.Delete(owned); }
            catch (IOException) when (!completed) { Console.Error.WriteLine("Destination probe cleanup failed; primary result retained."); }
        }
    }

    private static void Write<T>(T result) => Console.WriteLine(JsonSerializer.Serialize(result, ArchiveContract.Json));

    /// <summary>Exercise the published runtime's archive/crypto and DB-image tool dependencies without touching an installation.</summary>
    private static async Task RuntimeCheckAsync(CancellationToken token)
    {
        using var content = new MemoryStream();
        using (var gzip = new GZipStream(content, CompressionLevel.SmallestSize, leaveOpen: true))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Ustar, leaveOpen: true))
        {
            var entry = new UstarTarEntry(TarEntryType.RegularFile, "probe")
            {
                DataStream = new MemoryStream("Wayfarer recovery runtime"u8.ToArray()),
                ModificationTime = DateTimeOffset.UnixEpoch
            };
            tar.WriteEntry(entry);
        }
        content.Position = 0;
        var digest = Convert.ToHexStringLower(SHA256.HashData(content));
        var version = await DatabaseCapture.RunAsync("pg_dump", ["--version"], null, token);
        if (!version.StartsWith("pg_dump (PostgreSQL) 18.")) throw new IOException("Unsupported dump tool.");
        Write(new { Schema = 1, Runtime = "ready", Digest = digest, Tool = version.Trim(),
            Version = typeof(WorkerCli).Assembly.GetName().Version!.ToString() });
    }
}
