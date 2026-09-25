using Npgsql;

namespace Wayfarer.Services;

/// <summary>Resolves the one opt-in database password file without exposing its content.</summary>
internal static class DatabaseSecret
{
    /// <summary>Overrides only the password; native connection strings remain unchanged without this setting.</summary>
    internal static void Apply(IConfiguration configuration)
    {
        var path = configuration["Database:PasswordFile"];
        if (path is null) return;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException();
            var password = File.ReadAllText(path).TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(password)) throw new IOException();
            var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("DefaultConnection"));
            connection.Password = password;
            configuration["ConnectionStrings:DefaultConnection"] = connection.ConnectionString;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            throw new InvalidOperationException("Database password file is missing, unreadable, empty or invalid.");
        }
    }
}
