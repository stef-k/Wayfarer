using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

/// <summary>
/// Handles Quartz schema installation and migrations.
/// </summary>
public static class QuartzSchemaInstaller
{
    /// <summary>
    /// Job type name mappings from short names to fully qualified names.
    /// Used to fix legacy entries that were stored with incorrect type names.
    /// </summary>
    private static readonly Dictionary<string, string> JobTypeNameMappings = new()
    {
        ["LogCleanupJob, Wayfarer"] = "Wayfarer.Jobs.LogCleanupJob, Wayfarer",
        ["AuditLogCleanupJob, Wayfarer"] = "Wayfarer.Jobs.AuditLogCleanupJob, Wayfarer",
        ["VisitCleanupJob, Wayfarer"] = "Wayfarer.Jobs.VisitCleanupJob, Wayfarer",
        ["LocationImportJob, Wayfarer"] = "Wayfarer.Jobs.LocationImportJob, Wayfarer"
    };

    /// <summary>
    /// Ensures Quartz tables exist and migrates any legacy job type names.
    /// </summary>
    public static async Task EnsureQuartzTablesExistAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var connection = dbContext.Database.GetDbConnection();

        await connection.OpenAsync();

        var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM information_schema.tables WHERE table_name = 'qrtz_job_details'";
        var result = await command.ExecuteScalarAsync();

        if (Convert.ToInt32(result) == 0)
        {
            var ddlSql = await LoadEmbeddedSqlAsync("tables_postgres.sql");
            var ddlCommands = ddlSql.Split(";", StringSplitOptions.RemoveEmptyEntries);

            foreach (var cmd in ddlCommands)
            {
                var trimmedCmd = cmd.Trim();
                if (!string.IsNullOrWhiteSpace(trimmedCmd))
                {
                    using var ddlCommand = connection.CreateCommand();
                    ddlCommand.CommandText = trimmedCmd;

                    try
                    {
                        await ddlCommand.ExecuteNonQueryAsync();
                    }
                    catch (Exception ex)
                    {
                        // Log or handle the exception as needed
                        Console.WriteLine($"Error executing SQL command: {trimmedCmd}. Exception: {ex.Message}");
                        throw;
                    }
                }
            }
        }
        else
        {
            // Tables exist - run migration to fix any legacy job type names
            await MigrateJobTypeNamesAsync(connection);
        }
    }

    /// <summary>
    /// Migrates legacy job type names (short names) to fully qualified names.
    /// This fixes issues where jobs were stored with names like "AuditLogCleanupJob, Wayfarer"
    /// instead of "Wayfarer.Jobs.AuditLogCleanupJob, Wayfarer".
    /// </summary>
    private static async Task MigrateJobTypeNamesAsync(System.Data.Common.DbConnection connection)
    {
        foreach (var mapping in JobTypeNameMappings)
        {
            using var updateCommand = connection.CreateCommand();
            updateCommand.CommandText = @"
                UPDATE qrtz_job_details
                SET job_class_name = @newName
                WHERE job_class_name = @oldName";

            var oldNameParam = updateCommand.CreateParameter();
            oldNameParam.ParameterName = "@oldName";
            oldNameParam.Value = mapping.Key;
            updateCommand.Parameters.Add(oldNameParam);

            var newNameParam = updateCommand.CreateParameter();
            newNameParam.ParameterName = "@newName";
            newNameParam.Value = mapping.Value;
            updateCommand.Parameters.Add(newNameParam);

            var rowsAffected = await updateCommand.ExecuteNonQueryAsync();
            if (rowsAffected > 0)
            {
                Console.WriteLine($"[QuartzMigration] Updated {rowsAffected} job(s): '{mapping.Key}' -> '{mapping.Value}'");
            }
        }
    }


    private static async Task<string> LoadEmbeddedSqlAsync(string fileName)
    {
        var assembly = typeof(QuartzSchemaInstaller).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(name => name.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));

        if (resourceName == null)
            throw new FileNotFoundException($"Embedded resource '{fileName}' not found.");

        using var stream = assembly.GetManifestResourceStream(resourceName);
        using var reader = new StreamReader(stream!);
        return await reader.ReadToEndAsync();
    }
}