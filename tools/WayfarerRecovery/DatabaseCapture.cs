using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using Npgsql;

namespace WayfarerRecovery;

/// <summary>Non-superuser custom dump and identity from one exported read-only database snapshot.</summary>
public static class DatabaseCapture
{
    /// <summary>Keep password bytes private, use a task-local passfile, and validate before releasing the snapshot.</summary>
    public static async Task<(RecoveryComponent Component, DatabaseIdentity Identity)> CaptureAsync(
        string staging, string passwordFile, SourceIdentity source, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        var password = (await File.ReadAllTextAsync(passwordFile, token)).TrimEnd('\r', '\n');
        if (password.Length != 64 || password.Any(c => !Uri.IsHexDigit(c))) throw new IOException("Unsupported application credential.");
        var passfile = Path.Combine(staging, "pgpass");
        var settings = new NpgsqlConnectionStringBuilder
        {
            Host = "db", Database = "wayfarer", Username = "wayfarer", Password = password,
            Timeout = 5, CommandTimeout = 15, Pooling = false, ApplicationName = "wayfarer-recovery"
        };
        try
        {
            await using (var output = new FileStream(passfile, new FileStreamOptions
            { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite }))
            {
                await using var writer = new StreamWriter(output);
                await writer.WriteAsync($"db:5432:wayfarer:wayfarer:{password}\n".AsMemory(), token);
            }
            await using var connection = new NpgsqlConnection(settings.ConnectionString);
            await connection.OpenAsync(token);
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, token);
            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                await readOnly.ExecuteNonQueryAsync(token);
            var identity = await IdentityAsync(connection, transaction, token);
            if (identity.Major != 17 || identity.PostgisExtension != "3.6.4" || identity.PostgisExtension != identity.PostgisLibrary ||
                identity.Citext != "1.6" || identity.Encoding != "UTF8" || identity.Collation != "C.UTF-8" ||
                identity.CharacterType != "C.UTF-8" || identity.LocaleProvider != "c" ||
                identity.Migrations.Length == 0 || !identity.Migrations.SequenceEqual(source.ExpectedMigrations))
                throw new IOException("Unsupported database/schema identity.");
            await using var export = new NpgsqlCommand("SELECT pg_export_snapshot()", connection, transaction);
            var snapshot = (string)(await export.ExecuteScalarAsync(token) ?? throw new IOException("Snapshot unavailable."));
            var path = Path.Combine(staging, "database.dump");
            await RunAsync("pg_dump", ["--host=db", "--username=wayfarer", "--dbname=wayfarer", "--no-password",
                "--format=custom", "--lock-wait-timeout=10000", "--snapshot=" + snapshot, "--file=" + path], passfile, token);
            await RunAsync("pg_restore", ["--list", path], null, token);
            identity = identity with
            {
                DumpVersion = (await RunAsync("pg_dump", ["--version"], null, token)).Trim(),
                RestoreVersion = (await RunAsync("pg_restore", ["--version"], null, token)).Trim()
            };
            await transaction.CommitAsync(token);
            await using var dump = File.OpenRead(path);
            var component = new RecoveryComponent("database", "database.dump", dump.Length,
                Convert.ToHexStringLower(await SHA256.HashDataAsync(dump, token)), started, DateTimeOffset.UtcNow);
            return (component, identity);
        }
        finally
        {
            try { if (File.Exists(passfile)) File.Delete(passfile); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine("Private credential cleanup failed; primary result retained."); }
        }
    }

    private static async Task<DatabaseIdentity> IdentityAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        const string sql = """
            SELECT current_setting('server_version'), current_setting('server_version_num')::int / 10000,
                current_database(), (SELECT extversion FROM pg_extension WHERE extname='postgis'), postgis_lib_version(),
                (SELECT extversion FROM pg_extension WHERE extname='citext'), pg_encoding_to_char(encoding),
                datcollate, datctype, datlocprovider::text, datlocale
            FROM pg_database WHERE datname=current_database()
            """;
        DatabaseIdentity identity;
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        await using (var row = await command.ExecuteReaderAsync(token))
        {
            if (!await row.ReadAsync(token)) throw new IOException("Database identity unavailable.");
            identity = new DatabaseIdentity
            {
                ServerVersion = row.GetString(0), Major = row.GetInt32(1), Name = row.GetString(2),
                PostgisExtension = row.GetString(3), PostgisLibrary = row.GetString(4), Citext = row.GetString(5),
                Encoding = row.GetString(6), Collation = row.GetString(7), CharacterType = row.GetString(8),
                LocaleProvider = row.GetString(9), Locale = row.IsDBNull(10) ? null : row.GetString(10)
            };
        }
        var migrations = new List<string>();
        await using var history = new NpgsqlCommand("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\" ORDER BY \"MigrationId\"", connection, transaction);
        await using var reader = await history.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            if (migrations.Count >= 10000) throw new IOException("Migration history limit exceeded.");
            migrations.Add(reader.GetString(0));
        }
        return identity with { Migrations = migrations.ToArray(), TerminalMigration = migrations.LastOrDefault() ?? "" };
    }

    /// <summary>Fixed argument vectors, bounded private diagnostics, and cancellation that reaps the actual child.</summary>
    public static async Task<string> RunAsync(string executable, string[] arguments, string? passfile, CancellationToken token)
    {
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var name in start.Environment.Keys.Where(key => key.StartsWith("PG", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(name);
        start.Environment["PGCONNECT_TIMEOUT"] = "5";
        if (passfile is not null) start.Environment["PGPASSFILE"] = passfile;
        using var process = Process.Start(start) ?? throw new IOException("Database tool unavailable.");
        var output = DrainAsync(process.StandardOutput);
        var error = DrainAsync(process.StandardError);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            throw;
        }
        await error;
        if (process.ExitCode != 0) throw new IOException("Database tool failed.");
        return await output;
    }

    private static async Task<string> DrainAsync(StreamReader reader)
    {
        var result = new System.Text.StringBuilder();
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0)
            if (result.Length < 4096) result.Append(buffer, 0, Math.Min(count, 4096 - result.Length));
        return result.ToString();
    }
}
