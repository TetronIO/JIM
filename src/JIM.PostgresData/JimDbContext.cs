using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData
{
    internal class JimDbContext : DbContext
    {
        internal DbSet<MetaverseObjectType> MetaverseObjectTypes { get; set; }
        internal DbSet<MetaverseAttribute> MetaverseAttributes { get; set; }
        internal DbSet<MetaverseObjectAttributeValue> MetaverseObjectAttributeValues { get; set; }
        internal DbSet<MetaverseObject> MetaverseObjects { get; set; }
        internal DbSet<SyncRule> SyncRules { get; set; }
        internal DbSet<ConnectedSystem> ConnectedSystems { get; set; }
        internal DbSet<ConnectedSystemObject> ConnectedSystemObjects { get; set; }
        internal DbSet<ConnectedSystemObjectType> ConnectedSystemObjectTypes { get; set; }
        internal DbSet<ConnectedSystemAttribute> ConnectedSystemAttributes { get; set; }
        internal DbSet<SyncRun> SynchronisationRuns { get; set; }
        internal DbSet<ServiceSettings> ServiceSettings { get; set; }
        internal DbSet<Role> Roles { get; set; }

        private readonly string _connectionString;

        internal JimDbContext()
        {
            var dbHostName = Environment.GetEnvironmentVariable(Constants.CONFIG_DB_HOSTNAME);
            var dbName = Environment.GetEnvironmentVariable(Constants.CONFIG_DB_NAME);
            var dbUsername = Environment.GetEnvironmentVariable(Constants.CONFIG_DB_USERNAME);
            var dbPassword = Environment.GetEnvironmentVariable(Constants.CONFIG_DB_PASSWORD);

            if (string.IsNullOrEmpty(dbHostName))
                throw new Exception($"{Constants.CONFIG_DB_HOSTNAME} environment variable missing");
            if (string.IsNullOrEmpty(dbName))
                throw new Exception($"{Constants.CONFIG_DB_NAME} environment variable missing");
            if (string.IsNullOrEmpty(dbUsername))
                throw new Exception($"{Constants.CONFIG_DB_USERNAME} environment variable missing");
            if (string.IsNullOrEmpty(dbPassword))
                throw new Exception($"{Constants.CONFIG_DB_PASSWORD} environment variable missing");

            _connectionString = $"Host={dbHostName};Database={dbName};Username={dbUsername};Password={dbPassword}";
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseNpgsql(_connectionString);
    }
}
