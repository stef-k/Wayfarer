using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Wayfarer.Models;

public static class QuartzSchemaInstaller
{
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