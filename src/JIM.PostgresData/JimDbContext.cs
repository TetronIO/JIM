using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Logic;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;

namespace JIM.PostgresData
{
    public class JimDbContext : DbContext
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
        internal DbSet<ExampleDataSet> ExampleDataSets { get; set; }
        internal DbSet<ExampleDataValue> ExampleDataValues { get; set; }
        internal DbSet<DataGeneratorTemplate> DataGeneratorTemplates { get; set; }
        internal DbSet<DataGeneratorObjectType> DataGeneratorObjectTypes { get; set; }
        internal DbSet<DataGeneratorTemplateAttribute> DataGeneratorTemplateAttributes { get; set; }

        private readonly string _connectionString;

        public JimDbContext()
        {
            var dbHostName = Environment.GetEnvironmentVariable(Constants.Config.DatabaseHostname);
            var dbName = Environment.GetEnvironmentVariable(Constants.Config.DatabaseName);
            var dbUsername = Environment.GetEnvironmentVariable(Constants.Config.DatabaseUsername);
            var dbPassword = Environment.GetEnvironmentVariable(Constants.Config.DatabasePassword);

            if (string.IsNullOrEmpty(dbHostName))
                throw new Exception($"{Constants.Config.DatabaseHostname} environment variable missing");
            if (string.IsNullOrEmpty(dbName))
                throw new Exception($"{Constants.Config.DatabaseName} environment variable missing");
            if (string.IsNullOrEmpty(dbUsername))
                throw new Exception($"{Constants.Config.DatabaseUsername} environment variable missing");
            if (string.IsNullOrEmpty(dbPassword))
                throw new Exception($"{Constants.Config.DatabasePassword} environment variable missing");

            _connectionString = $"Host={dbHostName};Database={dbName};Username={dbUsername};Password={dbPassword}";
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) => optionsBuilder.UseNpgsql(_connectionString);

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<MetaverseObjectAttributeValue>()
                .HasOne(moav => moav.MetaverseObject)
                .WithMany(mo => mo.AttributeValues);

            modelBuilder.Entity<MetaverseObjectAttributeValue>()
                .HasOne(moav => moav.ReferenceValue)
                .WithMany();

            modelBuilder.Entity<MetaverseObject>()
                .HasMany(mo => mo.Roles)
                .WithMany(r => r.StaticMembers);
        }
    }
}
