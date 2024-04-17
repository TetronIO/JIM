using JIM.Data;
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
namespace JIM.PostgresData;

public class JimDbContext : DbContext
{
    public DbSet<Activity> Activities { get; set; } = null!;
    public DbSet<ActivityRunProfileExecutionItem> ActivityRunProfileExecutionItems { get; set; } = null!;
    public DbSet<ClearConnectedSystemObjectsWorkerTask> ClearConnectedSystemObjectsTasks { get; set; } = null!;
    public DbSet<ConnectedSystem> ConnectedSystems { get; set; } = null!;
    public DbSet<ConnectedSystemContainer> ConnectedSystemContainers { get; set; } = null!;
    public DbSet<ConnectedSystemObject> ConnectedSystemObjects { get; set; } = null!;
    public DbSet<ConnectedSystemObjectAttributeValue> ConnectedSystemObjectAttributeValues { get; set; } = null!;
    public DbSet<ConnectedSystemObjectChange> ConnectedSystemObjectChanges { get; set; } = null!;
    public DbSet<ConnectedSystemObjectChangeAttribute> ConnectedSystemObjectChangeAttributes { get; set; } = null!;
    public DbSet<ConnectedSystemObjectChangeAttributeValue> ConnectedSystemObjectChangeAttributeValues { get; set; } = null!;
    public DbSet<ConnectedSystemObjectType> ConnectedSystemObjectTypes { get; set; } = null!;
    public DbSet<ConnectedSystemObjectTypeAttribute> ConnectedSystemAttributes { get; set; } = null!;
    public DbSet<ConnectedSystemPartition> ConnectedSystemPartitions { get; set; } = null!;
    public DbSet<ConnectedSystemRunProfile> ConnectedSystemRunProfiles { get; set; } = null!;
    public DbSet<ConnectedSystemSettingValue> ConnectedSystemSettingValues { get; set; } = null!;
    public DbSet<ConnectorContainer> ConnectorContainers { get; set; } = null!;
    public DbSet<ConnectorDefinition> ConnectorDefinitions { get; set; } = null!;
    public DbSet<ConnectorDefinitionFile> ConnectorDefinitionFiles { get; set; } = null!;
    public DbSet<ConnectorDefinitionSetting> ConnectorDefinitionSettings { get; set; } = null!;
    public DbSet<ConnectorPartition> ConnectorPartitions { get; set; } = null!;
    public DbSet<DataGenerationObjectType> DataGenerationObjectTypes { get; set; } = null!;
    public DbSet<DataGenerationTemplate> DataGenerationTemplates { get; set; } = null!;
    public DbSet<DataGenerationTemplateAttribute> DataGenerationTemplateAttributes { get; set; } = null!;
    public DbSet<DataGenerationTemplateAttributeDependency> DataGenerationTemplateAttributeDependencies { get; set; } = null!;
    public DbSet<DataGenerationTemplateAttributeWeightedValue> DataGenerationTemplateAttributeWeightedValues { get; set; } = null!;
    public DbSet<DataGenerationTemplateWorkerTask> DataGenerationTemplateWorkerTasks { get; set; } = null!;
    public DbSet<ExampleDataSet> ExampleDataSets { get; set; } = null!;
    public DbSet<ExampleDataSetInstance> ExampleDataSetInstances { get; set; } = null!;
    public DbSet<ExampleDataSetValue> ExampleDataSetValues { get; set; } = null!;
    public DbSet<MetaverseAttribute> MetaverseAttributes { get; set; } = null!;
    public DbSet<MetaverseObject> MetaverseObjects { get; set; } = null!;
    public DbSet<MetaverseObjectAttributeValue> MetaverseObjectAttributeValues { get; set; } = null!;
    public DbSet<MetaverseObjectChange> MetaverseObjectChanges { get; set; } = null!;
    public DbSet<MetaverseObjectChangeAttribute> MetaverseObjectChangeAttributes { get; set; } = null!;
    public DbSet<MetaverseObjectChangeAttributeValue> MetaverseObjectChangeAttributeValues { get; set; } = null!;
    public DbSet<MetaverseObjectType> MetaverseObjectTypes { get; set; } = null!;
    public DbSet<PendingExport> PendingExports { get; set; } = null!;
    public DbSet<PendingExportAttributeValueChange> PendingExportAttributeValueChanges { get; set; } = null!;
    public DbSet<PredefinedSearch> PredefinedSearches { get; set; } = null!;
    public DbSet<PredefinedSearchAttribute> PredefinedSearchAttributes {  get; set; } = null!;
    public DbSet<PredefinedSearchCriteria> PredefinedSearchCriteria { get; set; } = null!;
    public DbSet<PredefinedSearchCriteriaGroup> PredefinedSearchCriteriaGroups { get; set; } = null!;
    public DbSet<Role> Roles { get; set; } = null!;
    public DbSet<ServiceSettings> ServiceSettings { get; set; } = null!;
    public DbSet<SyncRule> SyncRules { get; set; } = null!;
    public DbSet<SyncRuleMapping> SyncRuleMappings { get; set; } = null!;
    public DbSet<SyncRuleMappingSource> SyncRuleMappingSources { get; set; } = null!;
    public DbSet<SyncRuleMappingSourceParamValue> SyncRuleMappingSourceParamValues { get; set; } = null!;
    public DbSet<SyncRuleScopingCriteria> SyncRuleScopingCriteria { get; set; } = null!;
    public DbSet<SyncRuleScopingCriteriaGroup> SyncRuleScopingCriteriaGroups { get; set; } = null!;
    public DbSet<SynchronisationWorkerTask> SynchronisationWorkerTasks { get; set; } = null!;
    public DbSet<WorkerTask> WorkerTasks { get; set; } = null!;

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