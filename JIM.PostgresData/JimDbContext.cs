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
    public virtual DbSet<Activity> Activities { get; set; } = null!;
    public virtual DbSet<ActivityRunProfileExecutionItem> ActivityRunProfileExecutionItems { get; set; } = null!;
    public virtual DbSet<ClearConnectedSystemObjectsWorkerTask> ClearConnectedSystemObjectsTasks { get; set; } = null!;
    public virtual DbSet<ConnectedSystem> ConnectedSystems { get; set; } = null!;
    public virtual DbSet<ConnectedSystemContainer> ConnectedSystemContainers { get; set; } = null!;
    public virtual DbSet<ConnectedSystemObject> ConnectedSystemObjects { get; set; } = null!;
    public virtual DbSet<ConnectedSystemObjectAttributeValue> ConnectedSystemObjectAttributeValues { get; set; } = null!;
    public virtual DbSet<ConnectedSystemObjectChange> ConnectedSystemObjectChanges { get; set; } = null!;
    public virtual DbSet<ConnectedSystemObjectChangeAttribute> ConnectedSystemObjectChangeAttributes { get; set; } = null!;
    public virtual DbSet<ConnectedSystemObjectChangeAttributeValue> ConnectedSystemObjectChangeAttributeValues { get; set; } = null!;
    public virtual DbSet<ConnectedSystemObjectType> ConnectedSystemObjectTypes { get; set; } = null!;
    public virtual DbSet<ConnectedSystemObjectTypeAttribute> ConnectedSystemAttributes { get; set; } = null!;
    public virtual DbSet<ConnectedSystemPartition> ConnectedSystemPartitions { get; set; } = null!;
    public virtual DbSet<ConnectedSystemRunProfile> ConnectedSystemRunProfiles { get; set; } = null!;
    public virtual DbSet<ConnectedSystemSettingValue> ConnectedSystemSettingValues { get; set; } = null!;
    public virtual DbSet<ConnectorContainer> ConnectorContainers { get; set; } = null!;
    public virtual DbSet<ConnectorDefinition> ConnectorDefinitions { get; set; } = null!;
    public virtual DbSet<ConnectorDefinitionFile> ConnectorDefinitionFiles { get; set; } = null!;
    public virtual DbSet<ConnectorDefinitionSetting> ConnectorDefinitionSettings { get; set; } = null!;
    public virtual DbSet<ConnectorPartition> ConnectorPartitions { get; set; } = null!;
    public virtual DbSet<DataGenerationObjectType> DataGenerationObjectTypes { get; set; } = null!;
    public virtual DbSet<DataGenerationTemplate> DataGenerationTemplates { get; set; } = null!;
    public virtual DbSet<DataGenerationTemplateAttribute> DataGenerationTemplateAttributes { get; set; } = null!;
    public virtual DbSet<DataGenerationTemplateAttributeDependency> DataGenerationTemplateAttributeDependencies { get; set; } = null!;
    public virtual DbSet<DataGenerationTemplateAttributeWeightedValue> DataGenerationTemplateAttributeWeightedValues { get; set; } = null!;
    public virtual DbSet<DataGenerationTemplateWorkerTask> DataGenerationTemplateWorkerTasks { get; set; } = null!;
    public virtual DbSet<ExampleDataSet> ExampleDataSets { get; set; } = null!;
    public virtual DbSet<ExampleDataSetInstance> ExampleDataSetInstances { get; set; } = null!;
    public virtual DbSet<ExampleDataSetValue> ExampleDataSetValues { get; set; } = null!;
    public virtual DbSet<MetaverseAttribute> MetaverseAttributes { get; set; } = null!;
    public virtual DbSet<MetaverseObject> MetaverseObjects { get; set; } = null!;
    public virtual DbSet<MetaverseObjectAttributeValue> MetaverseObjectAttributeValues { get; set; } = null!;
    public virtual DbSet<MetaverseObjectChange> MetaverseObjectChanges { get; set; } = null!;
    public virtual DbSet<MetaverseObjectChangeAttribute> MetaverseObjectChangeAttributes { get; set; } = null!;
    public virtual DbSet<MetaverseObjectChangeAttributeValue> MetaverseObjectChangeAttributeValues { get; set; } = null!;
    public virtual DbSet<MetaverseObjectType> MetaverseObjectTypes { get; set; } = null!;
    public virtual DbSet<PendingExport> PendingExports { get; set; } = null!;
    public virtual DbSet<PendingExportAttributeValueChange> PendingExportAttributeValueChanges { get; set; } = null!;
    public virtual DbSet<PredefinedSearch> PredefinedSearches { get; set; } = null!;
    public virtual DbSet<PredefinedSearchAttribute> PredefinedSearchAttributes {  get; set; } = null!;
    public virtual DbSet<PredefinedSearchCriteria> PredefinedSearchCriteria { get; set; } = null!;
    public virtual DbSet<PredefinedSearchCriteriaGroup> PredefinedSearchCriteriaGroups { get; set; } = null!;
    public virtual DbSet<Role> Roles { get; set; } = null!;
    public virtual DbSet<ServiceSettings> ServiceSettings { get; set; } = null!;
    public virtual DbSet<SyncRule> SyncRules { get; set; } = null!;
    public virtual DbSet<SyncRuleMapping> SyncRuleMappings { get; set; } = null!;
    public virtual DbSet<SyncRuleMappingSource> SyncRuleMappingSources { get; set; } = null!;
    public virtual DbSet<SyncRuleMappingSourceParamValue> SyncRuleMappingSourceParamValues { get; set; } = null!;
    public virtual DbSet<SyncRuleScopingCriteria> SyncRuleScopingCriteria { get; set; } = null!;
    public virtual DbSet<SyncRuleScopingCriteriaGroup> SyncRuleScopingCriteriaGroups { get; set; } = null!;
    public virtual DbSet<SynchronisationWorkerTask> SynchronisationWorkerTasks { get; set; } = null!;
    public virtual DbSet<WorkerTask> WorkerTasks { get; set; } = null!;

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

        _ = bool.TryParse(dbLogSensitiveInfo, out var logSensitiveInfo);
        if (logSensitiveInfo)
            _connectionString += ";Include Error Detail=True";
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        optionsBuilder.UseNpgsql(_connectionString)
            .ConfigureWarnings(warnings => warnings.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ConnectedSystemObject>()
            .HasMany(cso => cso.AttributeValues)
            .WithOne(av => av.ConnectedSystemObject)
            .OnDelete(DeleteBehavior.Cascade); // let the db delete all dependent ConnectedSystemAttributeValue objects when the CSO is deleted.

        modelBuilder.Entity<ConnectedSystemObject>()
            .HasMany(cso => cso.ActivityRunProfileExecutionItems)
            .WithOne(i => i.ConnectedSystemObject)
            .OnDelete(DeleteBehavior.SetNull); // let the db clear the fk value to the CSO.
        
        modelBuilder.Entity<ConnectedSystemObject>()
            .HasMany(cso => cso.Changes)
            .WithOne(c => c.ConnectedSystemObject)
            .OnDelete(DeleteBehavior.SetNull); // let the db clear the fk value to the CSO.
        
        modelBuilder.Entity<ConnectedSystemObjectChange>()
            .HasMany(cso => cso.AttributeChanges)
            .WithOne(ac => ac.ConnectedSystemChange)
            .OnDelete(DeleteBehavior.Cascade); // let the db delete all dependent ConnectedSystemObjectChangeAttribute objects when the parent is deleted.

        modelBuilder.Entity<ConnectedSystemObjectChangeAttribute>()
            .HasMany(ca => ca.ValueChanges)
            .WithOne(av => av.ConnectedSystemObjectChangeAttribute)
            .OnDelete(DeleteBehavior.Cascade); // let the db delete all dependent ConnectedSystemObjectChangeAttributeValue objects when the parent is deleted.

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
        
        modelBuilder.Entity<MetaverseObjectAttributeValue>()
            .HasOne(moav => moav.UnresolvedReferenceValue)
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
        // Note: In Npgsql.EntityFrameworkCore.PostgreSQL 7.0+, UseXminAsConcurrencyToken() is obsolete.
        // Use the standard EF Core approach with a uint xmin property and IsRowVersion() instead.
        modelBuilder.Entity<MetaverseObject>()
            .Property(e => e.xmin)
            .IsRowVersion();
    }
}