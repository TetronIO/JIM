using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Update;

namespace JIM.PostgresData
{
    public class JimDbContext : DbContext
    {
        internal DbSet<Activity> Activities { get; set; } = null!;
        internal DbSet<ActivityRunProfileExecutionItem> ActivityRunProfileExecutionItems { get; set; } = null!;
        internal DbSet<ClearConnectedSystemObjectsWorkerTask> ClearConnectedSystemObjectsTasks { get; set; } = null!;
        internal DbSet<ConnectedSystem> ConnectedSystems { get; set; } = null!;
        internal DbSet<ConnectedSystemContainer> ConnectedSystemContainers { get; set; } = null!;
        internal DbSet<ConnectedSystemObject> ConnectedSystemObjects { get; set; } = null!;
        internal DbSet<ConnectedSystemObjectAttributeValue> ConnectedSystemObjectAttributeValues { get; set; } = null!;
        internal DbSet<ConnectedSystemObjectChange> ConnectedSystemObjectChanges { get; set; } = null!;
        internal DbSet<ConnectedSystemObjectChangeAttribute> ConnectedSystemObjectChangeAttributes { get; set; } = null!;
        internal DbSet<ConnectedSystemObjectChangeAttributeValue> ConnectedSystemObjectChangeAttributeValues { get; set; } = null!;
        internal DbSet<ConnectedSystemObjectType> ConnectedSystemObjectTypes { get; set; } = null!;
        internal DbSet<ConnectedSystemObjectTypeAttribute> ConnectedSystemAttributes { get; set; } = null!;
        internal DbSet<ConnectedSystemPartition> ConnectedSystemPartitions { get; set; } = null!;
        internal DbSet<ConnectedSystemRunProfile> ConnectedSystemRunProfiles { get; set; } = null!;
        internal DbSet<ConnectorDefinition> ConnectorDefinitions { get; set; } = null!;
        internal DbSet<ConnectorDefinitionFile> ConnectorDefinitionFiles { get; set; } = null!;
        internal DbSet<DataGenerationObjectType> DataGenerationObjectTypes { get; set; } = null!;
        internal DbSet<DataGenerationTemplate> DataGenerationTemplates { get; set; } = null!;
        internal DbSet<DataGenerationTemplateAttribute> DataGenerationTemplateAttributes { get; set; } = null!;
        internal DbSet<DataGenerationTemplateWorkerTask> DataGenerationTemplateWorkerTasks { get; set; } = null!;
        internal DbSet<ExampleDataSet> ExampleDataSets { get; set; } = null!;
        internal DbSet<ExampleDataSetInstance> ExampleDataSetInstances { get; set; } = null!;
        internal DbSet<ExampleDataSetValue> ExampleDataSetValues { get; set; } = null!;
        internal DbSet<MetaverseAttribute> MetaverseAttributes { get; set; } = null!;
        internal DbSet<MetaverseObject> MetaverseObjects { get; set; } = null!;
        internal DbSet<MetaverseObjectAttributeValue> MetaverseObjectAttributeValues { get; set; } = null!;
        internal DbSet<MetaverseObjectChange> MetaverseObjectChanges { get; set; } = null!;
        internal DbSet<MetaverseObjectChangeAttribute> MetaverseObjectChangeAttributes { get; set; } = null!;
        internal DbSet<MetaverseObjectChangeAttributeValue> MetaverseObjectChangeAttributeValues { get; set; } = null!;
        internal DbSet<MetaverseObjectType> MetaverseObjectTypes { get; set; } = null!;
        internal DbSet<PendingExport> PendingExports { get; set; } = null!;
        internal DbSet<PendingExportAttributeValueChange> PendingExportAttributeValueChanges { get; set; } = null!;
        internal DbSet<PredefinedSearch> PredefinedSearches { get; set; } = null!;
        internal DbSet<PredefinedSearchCriteria> PredefinedSearchCriteria { get; set; } = null!;
        internal DbSet<PredefinedSearchCriteriaGroup> PredefinedSearchCriteriaGroups { get; set; } = null!;
        internal DbSet<Role> Roles { get; set; } = null!;
        internal DbSet<ServiceSettings> ServiceSettings { get; set; } = null!;
        internal DbSet<WorkerTask> WorkerTasks { get; set; } = null!;
        internal DbSet<SynchronisationWorkerTask> SynchronisationWorkerTasks { get; set; } = null!;
        internal DbSet<SyncRule> SyncRules { get; set; } = null!;

        private readonly string _connectionString;

        public JimDbContext()
        {
            var dbHostName = Environment.GetEnvironmentVariable(Constants.Config.DatabaseHostname);
            var dbName = Environment.GetEnvironmentVariable(Constants.Config.DatabaseName);
            var dbUsername = Environment.GetEnvironmentVariable(Constants.Config.DatabaseUsername);
            var dbPassword = Environment.GetEnvironmentVariable(Constants.Config.DatabasePassword);
            var dbLogSensitiveInfo = Environment.GetEnvironmentVariable(Constants.Config.DatabaseLogSensitiveInformation);

            if (string.IsNullOrEmpty(dbHostName))
                throw new Exception($"{Constants.Config.DatabaseHostname} environment variable missing");
            if (string.IsNullOrEmpty(dbName))
                throw new Exception($"{Constants.Config.DatabaseName} environment variable missing");
            if (string.IsNullOrEmpty(dbUsername))
                throw new Exception($"{Constants.Config.DatabaseUsername} environment variable missing");
            if (string.IsNullOrEmpty(dbPassword))
                throw new Exception($"{Constants.Config.DatabasePassword} environment variable missing");

            _connectionString = $"Host={dbHostName};Database={dbName};Username={dbUsername};Password={dbPassword}";

            _ = bool.TryParse(dbLogSensitiveInfo, out bool logSensitiveInfo);
            if (logSensitiveInfo)
                _connectionString += ";Include Error Detail=True";
        }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            optionsBuilder.UseNpgsql(_connectionString);
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ConnectedSystemObject>()
                .HasMany(cso => cso.AttributeValues)
                .WithOne(csoav => csoav.ConnectedSystemObject);

            modelBuilder.Entity<ConnectedSystemObject>()
                .HasMany(cso => cso.ActivityRunProfileExecutionItems)
                .WithOne(i => i.ConnectedSystemObject)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ConnectedSystemObjectType>()
                .HasMany(csot => csot.Attributes)
                .WithOne(csa => csa.ConnectedSystemObjectType);

            modelBuilder.Entity<MetaverseObject>()
                .HasMany(mo => mo.Roles)
                .WithMany(r => r.StaticMembers);

            modelBuilder.Entity<MetaverseObject>()
                .HasMany(mvo => mvo.Changes)
                .WithOne(mvoc => mvoc.MetaverseObject);

            modelBuilder.Entity<MetaverseObjectAttributeValue>()
                .HasOne(moav => moav.MetaverseObject)
                .WithMany(mo => mo.AttributeValues);

            modelBuilder.Entity<MetaverseObjectAttributeValue>()
                .HasOne(moav => moav.ReferenceValue)
                .WithMany();

            modelBuilder.Entity<MetaverseObjectType>()
                .HasMany(mot => mot.Attributes);

            modelBuilder.Entity<SyncRule>()
                .HasMany(sr => sr.AttributeFlowRules)
                .WithOne(afr => afr.AttributeFlowSynchronisationRule);

            modelBuilder.Entity<SyncRule>()
                .HasMany(sr => sr.ObjectMatchingRules)
                .WithOne(omr => omr.ObjectMatchingSynchronisationRule);

            // reduce the chance of concurrency issues by using a system attribute to identify row versions
            // for our most heavily updated objects.
            // https://www.npgsql.org/efcore/modeling/concurrency.html?tabs=data-annotations
            // https://learn.microsoft.com/en-us/ef/core/saving/concurrency?tabs=data-annotations
            modelBuilder.Entity<MetaverseObject>().UseXminAsConcurrencyToken();
        }
    }
}
