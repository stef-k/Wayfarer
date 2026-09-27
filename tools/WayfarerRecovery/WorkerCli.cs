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
        if (!OperatingSystem.IsLinux() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return 2;
        umask(0x3f);
        try
        {
            if (arguments is ["runtime-check"])
            {
                await RuntimeCheckAsync(token);
                return 0;
            }
            if (arguments is not (["backup"] or ["backups"] or ["verify"] or ["verify", _])) return 2;
            var config = WorkerConfiguration.Load("/config/worker.json");
            var engine = new RecoveryEngine(config);
            if (arguments[0] == "backup")
            {
                var result = await engine.BackupAsync(null, token);
                Write(result);
                return result.RetentionSucceeded ? 0 : 1;
            }
            using var exclusion = new RecoveryLock("/control/recovery.lock");
            using var destination = config.OpenDestination();
            var manifests = engine.List(destination);
            if (arguments[0] == "backups")
            {
                Write(new { Schema = 1, Verification = "not-fully-verified", More = manifests.Length > 20,
                    Archives = manifests.Take(20).Select(value => new
                    {
                        value.Archive, value.Completed, value.Mode,
                        Name = ArchiveContract.Name(value.Installation, value.Completed, value.Archive)
                    }) });
                return 0;
            }
            var name = arguments.Length == 2 ? arguments[1] : manifests.Length > 0
                ? ArchiveContract.Name(config.Installation, manifests[0].Completed, manifests[0].Archive)
                : throw new IOException("No complete owned archive.");
            using var staging = new RecoveryTaskDirectory();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(config.DeadlineSeconds));
            var verified = await ArchiveVerifier.VerifyAsync(destination, name, staging.Path, config.Source, config.Installation, deadline.Token);
            Write(verified);
            return verified.CompatibilitySupported ? 0 : 1;
        }
        catch (OperationCanceledException) { Write(new { Schema = 1, Success = false, Failure = "cancelled-or-deadline" }); return 1; }
        catch (Exception) { Write(new { Schema = 1, Success = false, Failure = "recovery-validation-or-operation-failed" }); return 1; }
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
        if (!version.StartsWith("pg_dump (PostgreSQL) 17.")) throw new IOException("Unsupported dump tool.");
        Write(new { Schema = 1, Runtime = "ready", Digest = digest, Tool = version.Trim() });
    }
}
