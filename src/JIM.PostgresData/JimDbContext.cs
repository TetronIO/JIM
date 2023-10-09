using JIM.Models.Core;
using JIM.Models.DataGeneration;
using JIM.Models.History;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Security;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics.Metrics;

namespace JIM.PostgresData
{
    public class JimDbContext : DbContext
    {
        internal DbSet<ClearConnectedSystemHistoryItem> ClearConnectedSystemHistoryItems { get; set; }
        internal DbSet<ClearConnectedSystemObjectsTask> ClearConnectedSystemObjectsTasks { get; set; }
        internal DbSet<ConnectedSystem> ConnectedSystems { get; set; }
        internal DbSet<ConnectedSystemContainer> ConnectedSystemContainers { get; set; }
        internal DbSet<ConnectedSystemObject> ConnectedSystemObjects { get; set; }
        internal DbSet<ConnectedSystemObjectAttributeValue> ConnectedSystemObjectAttributeValues { get; set; }
        internal DbSet<ConnectedSystemObjectChange> ConnectedSystemObjectChanges { get; set; }
        internal DbSet<ConnectedSystemObjectChangeAttribute> ConnectedSystemObjectChangeAttributes { get; set; }
        internal DbSet<ConnectedSystemObjectChangeAttributeValue> ConnectedSystemObjectChangeAttributeValues { get; set; }
        internal DbSet<ConnectedSystemObjectType> ConnectedSystemObjectTypes { get; set; }
        internal DbSet<ConnectedSystemObjectTypeAttribute> ConnectedSystemAttributes { get; set; }
        internal DbSet<ConnectedSystemPartition> ConnectedSystemPartitions { get; set; }
        internal DbSet<ConnectedSystemRunProfile> ConnectedSystemRunProfiles { get; set; }
        internal DbSet<ConnectorDefinition> ConnectorDefinitions { get; set; }
        internal DbSet<ConnectorDefinitionFile> ConnectorDefinitionFiles { get; set; }
        internal DbSet<DataGenerationObjectType> DataGenerationObjectTypes { get; set; }
        internal DbSet<DataGenerationHistoryItem> DataGenerationHistoryItems { get; set; }
        internal DbSet<DataGenerationTemplate> DataGenerationTemplates { get; set; }
        internal DbSet<DataGenerationTemplateAttribute> DataGenerationTemplateAttributes { get; set; }
        internal DbSet<DataGenerationTemplateServiceTask> DataGenerationTemplateServiceTasks { get; set; }
        internal DbSet<ExampleDataSet> ExampleDataSets { get; set; }
        internal DbSet<ExampleDataSetInstance> ExampleDataSetInstances { get; set; }
        internal DbSet<ExampleDataSetValue> ExampleDataSetValues { get; set; }
        internal DbSet<HistoryItem> HistoryItems { get; set; }
        internal DbSet<MetaverseAttribute> MetaverseAttributes { get; set; }
        internal DbSet<MetaverseObject> MetaverseObjects { get; set; }
        internal DbSet<MetaverseObjectAttributeValue> MetaverseObjectAttributeValues { get; set; }
        internal DbSet<MetaverseObjectChange> MetaverseObjectChanges { get; set; }
        internal DbSet<MetaverseObjectChangeAttribute> MetaverseObjectChangeAttributes { get; set; }
        internal DbSet<MetaverseObjectChangeAttributeValue> MetaverseObjectChangeAttributeValues { get; set; }
        internal DbSet<MetaverseObjectType> MetaverseObjectTypes { get; set; }
        internal DbSet<PendingExport> PendingExports { get; set; }
        internal DbSet<PendingExportAttributeValueChange> PendingExportAttributeValueChanges { get; set; }
        internal DbSet<PredefinedSearch> PredefinedSearches { get; set; }
        internal DbSet<PredefinedSearchCriteria> PredefinedSearchCriteria { get; set; }
        internal DbSet<PredefinedSearchCriteriaGroup> PredefinedSearchCriteriaGroups { get; set; }
        internal DbSet<Role> Roles { get; set; }
        internal DbSet<RunHistoryItem> RunHistoryItems { get; set; }
        internal DbSet<ServiceSettings> ServiceSettings { get; set; }
        internal DbSet<ServiceTask> ServiceTasks { get; set; }
        internal DbSet<SynchronisationServiceTask> SynchronisationServiceTasks { get; set; }
        internal DbSet<SyncRule> SyncRules { get; set; }
        internal DbSet<SyncRunHistoryDetail> SyncRunHistoryDetails { get; set; }

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

            modelBuilder.Entity<SyncRunHistoryDetailItem>()
                .HasOne(a => a.ConnectedSystemObjectChange)
                .WithOne(a => a.SyncRunHistoryDetailItem)
                .HasForeignKey<ConnectedSystemObjectChange>(csoc => csoc.SyncRunHistoryDetailItemId);

            // reduce the chance of concurrency issues by using a system attribute to identify row versions
            // for our most heavily updated objects.
            // https://www.npgsql.org/efcore/modeling/concurrency.html?tabs=data-annotations
            // https://learn.microsoft.com/en-us/ef/core/saving/concurrency?tabs=data-annotations
            modelBuilder.Entity<MetaverseObject>().UseXminAsConcurrencyToken();
        }
    }
}
