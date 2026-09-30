using System.Text.Json;

namespace WayfarerCtl;

/// <summary>Build and validate a fresh candidate using only an internal isolated network and generated volumes.</summary>
public sealed class RestoreCandidate(IProcessRunner runner)
{
    public static string Network(RestorePlan plan) => plan.Target.Project + "-restore-" + plan.CandidateGeneration;
    public static Deployment Configuration(RestorePlan plan) => plan.Target with { Schema = plan.Target.Release is null ? 3 : 4, StorageGeneration = plan.CandidateGeneration };

    /// <summary>Persist every owned resource name before creation; failures retain inactive evidence.</summary>
    public async Task<RestoreReceipt> StageAsync(string root, RestoreReceipt receipt, CancellationToken token)
    {
        var plan = receipt.EffectivePlan;
        var candidate = Configuration(plan);
        var owner = new RestoreContainers(runner);
        var network = Network(plan);
        var directory = RestorePreparation.DirectoryFor(root, plan.Operation);
        var payload = File.ReadAllText(Path.Combine(directory, "payload"));
        var prefix = candidate.Project + "-restore-" + plan.CandidateGeneration;
        var db = prefix + "-db";
        var initialize = prefix + "-initialize";
        var files = prefix + "-files";
        var sql = prefix + "-sql";
        var inspect = prefix + "-inspect";
        receipt = receipt with
        {
            Containers = [.. receipt.Containers, db, initialize, files, sql, inspect],
            Volumes = [.. receipt.Volumes, .. new[] { "db-data", "app-data", "app-cache" }.Select(role => ActiveStorage.Volume(candidate, role))]
        };
        receipt.Save(root);
        await owner.Required(["network", "create", "--internal", "--label", "wayfarer.restore=" + plan.Operation.ToString("D"), "--label", "wayfarer.restore-helper=" + candidate.Project, network], token);
        var existingVolumes = (await owner.Required(["volume", "ls", "--format", "{{.Name}}"], token)).Split('\n');
        if (new[] { "db-data", "app-data", "app-cache" }.Any(role => existingVolumes.Contains(ActiveStorage.Volume(candidate, role))))
            throw new IOException("Candidate volume already exists; a fresh attempt is required.");
        foreach (var role in new[] { "db-data", "app-data", "app-cache" })
            await owner.Required(["volume", "create", "--label", "com.docker.compose.project=" + candidate.Project,
                "--label", "com.docker.compose.volume=" + role, "--label", "wayfarer.restore=" + plan.Operation.ToString("D"),
                ActiveStorage.Volume(candidate, role)], token);
        // Root sees only newly created empty roots, never archive bytes, old data, credentials or control storage.
        await owner.RunAsync(initialize, ["--network=none", "--read-only", "--user=0", "--cap-drop=ALL", "--cap-add=CHOWN",
            "--cap-add=FOWNER", "--security-opt=no-new-privileges:true", "--volume", ActiveStorage.Volume(candidate, "app-data") + ":/candidate",
            "--volume", ActiveStorage.Volume(candidate, "app-cache") + ":/cache", "--entrypoint=sh", "ghcr.io/stef-k/wayfarer@" + candidate.AppDigest,
            "-ec", "entries=$(find /candidate -mindepth 1 -maxdepth 1 -printf x -quit); test -z \"$entries\"; entries=$(find /cache -mindepth 1 -maxdepth 1 -printf x -quit); test -z \"$entries\"; chown 1654:1654 /candidate /cache; chmod 700 /candidate /cache"], token);
        await owner.RunAsync(files, [.. RestoreContainers.Unprivileged(), "--network=none", "--volume", payload + ":/worker:ro",
            "--volume", Path.Combine(directory, "verified") + ":/staging:ro", "--volume", ActiveStorage.Volume(candidate, "app-data") + ":/candidate",
            "--entrypoint=/worker", "ghcr.io/stef-k/wayfarer-db@" + candidate.DbDigest, "restore-files"], token);
        await owner.Required(["create", "--name", db, "--label", "wayfarer.restore-helper=" + candidate.Project, "--restart=no", "--pull=never", "--network", network, "--network-alias=db",
            "--volume", ActiveStorage.Volume(candidate, "db-data") + ":/var/lib/postgresql",
            "--volume", Path.Combine(root, "secrets/db-password") + ":/run/secrets/db-password:ro",
            "--volume", Path.Combine(root, "secrets/db-app-password") + ":/run/secrets/app-password:ro",
            "--volume", Path.Combine(candidate.Bundle, "db/20-wayfarer.sh") + ":/docker-entrypoint-initdb.d/20-wayfarer.sh:ro",
            "--env=POSTGRES_PASSWORD_FILE=/run/secrets/db-password", "--env=POSTGRES_INITDB_ARGS=--encoding=UTF8 --locale=C.UTF-8",
            "ghcr.io/stef-k/wayfarer-db@" + candidate.DbDigest], token);
        await owner.Required(["start", db], token);
        await WaitDatabaseAsync(db, token);
        var identity = await owner.Required(["exec", db, "psql", "-U", "postgres", "-d", "wayfarer", "-At", "-c",
            "SELECT current_setting('server_version'), " +
            "(SELECT extversion FROM pg_extension WHERE extname='postgis'), postgis_lib_version(), " +
            "(SELECT extversion FROM pg_extension WHERE extname='citext'), pg_encoding_to_char(encoding), " +
            "datcollate, datctype, datlocprovider, coalesce(datlocale,'') FROM pg_database WHERE datname=current_database();"], token);
        if (identity.Trim() != "18.6 (Debian 18.6-1.pgdg12+2)|3.6.4|3.6.4|1.8|UTF8|C.UTF-8|C.UTF-8|c|")
            throw new IOException("Candidate DB contract differs before SQL restore.");
        var secret = Path.Combine(directory, "bootstrap-secret-" + plan.CandidateGeneration);
        ProtectedFiles.Create(secret, File.ReadAllText(Path.Combine(root, "secrets/db-password")), 1654);
        await owner.RunAsync(sql, [.. RestoreContainers.Unprivileged(), "--network", network, "--volume", payload + ":/worker:ro",
            "--volume", Path.Combine(directory, "verified") + ":/staging:ro", "--volume", secret + ":/run/secrets/db-password:ro",
            "--entrypoint=/worker", "ghcr.io/stef-k/wayfarer-db@" + candidate.DbDigest, "restore-database"], token);
        var credentialStatus = await InspectAsync(root, candidate, directory, network, inspect, token);
        receipt = receipt with { ProtectedCredentialStatus = credentialStatus };
        receipt.Save(root);
        await owner.Required(["stop", "--time", "60", db], token);
        await owner.Required(["wait", db], token);
        return receipt;
    }

    /// <summary>Wait only for candidate bootstrap readiness; no host tools or production endpoint is used.</summary>
    private async Task WaitDatabaseAsync(string db, CancellationToken token)
    {
        for (var attempt = 0; attempt < 90; attempt++)
        {
            var result = await runner.RunAsync(["exec", db, "pg_isready", "-h", "127.0.0.1", "-U", "postgres", "-d", "wayfarer"], null, token);
            if (result.Code == 0) return;
            await Task.Delay(TimeSpan.FromSeconds(2), token);
        }
        throw new IOException("Candidate database bootstrap timeout.");
    }

    /// <summary>Product-owned schema, credentials and ring inspection has no edge/provider egress or writable ring.</summary>
    public async Task<string> InspectAsync(string root, Deployment candidate, string directory, string network, string name, CancellationToken token)
    {
        var payload = File.ReadAllText(Path.Combine(directory, "payload"));
        var inspector = Path.Combine(Path.GetDirectoryName(payload)!, "WayfarerRecoverySource.dll");
        var output = await new RestoreContainers(runner).RunAsync(name,
            [.. RestoreContainers.Unprivileged(), "--network", network, "--volume", inspector + ":/inspection.dll:ro",
                "--volume", ActiveStorage.Volume(candidate, "app-data") + ":/var/lib/wayfarer:ro",
                "--volume", Path.Combine(root, "secrets/app-password") + ":/run/secrets/app-password:ro",
                "--env=ConnectionStrings__DefaultConnection=Host=db;Database=wayfarer;Username=wayfarer;Timeout=5",
                "--env=Database__PasswordFile=/run/secrets/app-password", "--entrypoint=dotnet",
                "ghcr.io/stef-k/wayfarer@" + candidate.AppDigest, "exec", "--runtimeconfig", "/app/Wayfarer.runtimeconfig.json",
                "--depsfile", "/app/Wayfarer.deps.json", "/inspection.dll"], token);
        using var actual = JsonDocument.Parse(output);
        var expected = JsonSerializer.Deserialize<WayfarerRecovery.SourceIdentity>(File.ReadAllText(Path.Combine(directory, "source.json")))!;
        var facts = actual.RootElement;
        if (facts.GetProperty("ApplicationVersion").GetString() != expected.ApplicationVersion ||
            facts.GetProperty("ApplicationName").GetString() != "Wayfarer" || !QuartzMatches(facts, expected) ||
            !facts.GetProperty("ExpectedMigrations").Deserialize<string[]>()!.SequenceEqual(expected.ExpectedMigrations) ||
            facts.GetProperty("Uploads").GetString() != "uploads" || facts.GetProperty("Ring").GetString() != "data-protection")
            throw new IOException("Candidate product identity mismatch.");
        var credentials = facts.GetProperty("ProtectedCredentials").GetString();
        return credentials is "none present" or "readable" ? credentials : throw new IOException("Invalid protected credential evidence.");
    }
    /// <summary>Candidate inspection always validates product schema; only legacy targets demand physical equality.</summary>
    private static bool QuartzMatches(JsonElement facts, WayfarerRecovery.SourceIdentity expected) =>
        expected.ConfigurationSchema == 2
            ? facts.GetProperty("QuartzIdentity").GetString() == expected.QuartzIdentity
            : facts.GetProperty("Schema").GetInt32() == 2 &&
                facts.GetProperty("QuartzCompatibilityContract").GetString() == expected.QuartzCompatibilityContract;

}
