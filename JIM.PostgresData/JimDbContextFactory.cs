using Microsoft.EntityFrameworkCore.Design;

namespace JIM.PostgresData;

/// <summary>
/// Design-time factory for creating JimDbContext instances during migrations.
/// This allows EF Core tools to create migrations without requiring actual database connection.
/// </summary>
public class JimDbContextFactory : IDesignTimeDbContextFactory<JimDbContext>
{
    public JimDbContext CreateDbContext(string[] args)
    {
        // Set dummy environment variables for migration creation
        // These are only used at design-time and won't affect runtime
        Environment.SetEnvironmentVariable("DB_HOSTNAME", "localhost");
        Environment.SetEnvironmentVariable("DB_NAME", "jim_design");
        Environment.SetEnvironmentVariable("DB_USERNAME", "postgres");
        Environment.SetEnvironmentVariable("DB_PASSWORD", "password");
        Environment.SetEnvironmentVariable("DB_LOG_SENSITIVE_INFO", "false");

        return new JimDbContext();
    }
}