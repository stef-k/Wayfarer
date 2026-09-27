using System.Text.Json;

namespace WayfarerRecovery;

/// <summary>Fixed unprivileged restore operations reuse the archive verifier; host supplies isolated mounts.</summary>
public static class RestoreWorker
{
    /// <summary>No operation accepts a command, target path, image or connection string from an archive.</summary>
    public static async Task<int> RunAsync(string[] args, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromHours(1));
        token = deadline.Token;
        if (args is ["restore-select"])
        {
            var config = WorkerConfiguration.Load("/config/worker.json");
            using var destination = config.OpenDestination();
            var latest = new RecoveryEngine(config).List(destination, token).FirstOrDefault()
                ?? throw new IOException("No complete owned archive.");
            Console.WriteLine(ArchiveContract.Name(latest.Installation, latest.Completed, latest.Archive));
            return 0;
        }
        if (args is ["restore-verify", var name, var source])
        {
            if (!Guid.TryParseExact(source, "D", out var installation)) return 2;
            using var evidence = File.OpenRead("/target/source.json");
            if (evidence.Length > ArchiveContract.ManifestLimit) throw new IOException("Target evidence exceeds bound.");
            var expected = await JsonSerializer.DeserializeAsync<SourceIdentity>(evidence, ArchiveContract.Json, token)
                ?? throw new IOException("Target evidence missing.");
            ArchiveContract.ValidateSource(expected);
            using var frozen = new SafeDirectory("/frozen");
            var result = await ArchiveVerifier.VerifyAsync(frozen, name, "/staging", expected, installation, token);
            if (!result.CompatibilitySupported) throw new IOException("Incompatible archive.");
            using var manifestFile = File.OpenRead("/staging/manifest.json");
            var manifest = ArchiveContract.ReadManifest(manifestFile);
            if (manifest.Source.Kind != "compose" || manifest.Source.BundleFingerprint != expected.BundleFingerprint ||
                manifest.Source.PayloadFingerprint != expected.PayloadFingerprint || manifest.Database.Locale is not null)
                throw new IOException("Target bundle, capture payload or locale differs.");
            Console.WriteLine(JsonSerializer.Serialize(result, ArchiveContract.Json));
            return 0;
        }
        if (args is ["restore-files"])
        {
            // The initializer receives only an empty candidate root. This parser runs as UID1654.
            using var candidate = new SafeDirectory("/candidate");
            if (candidate.Names().Length != 0) throw new IOException("Candidate filesystem is not empty.");
            Directory.CreateDirectory("/candidate/data-protection", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Directory.CreateDirectory("/candidate/uploads", UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            ArchiveVerifier.ValidateDirectory("/staging/data-protection.tar.gz", "/candidate/data-protection", token);
            ArchiveVerifier.ValidateDirectory("/staging/uploads.tar.gz", "/candidate/uploads", token);
            return 0;
        }
        if (args is ["restore-database"])
        {
            var secret = await File.ReadAllTextAsync("/run/secrets/db-password", token);
            if (!System.Text.RegularExpressions.Regex.IsMatch(secret, "^[A-F0-9]{64}$")) throw new IOException("Invalid bootstrap secret.");
            const string passfile = "/tmp/restore.pgpass";
            await File.WriteAllTextAsync(passfile, "db:5432:wayfarer:postgres:" + secret + "\n", token);
            File.SetUnixFileMode(passfile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            try
            {
                await DatabaseCapture.RunAsync("pg_restore", ["--exit-on-error", "--host=db", "--username=postgres",
                    "--dbname=wayfarer", "/staging/database.dump"], passfile, token);
            }
            finally { File.Delete(passfile); }
            return 0;
        }
        return 2;
    }
}
