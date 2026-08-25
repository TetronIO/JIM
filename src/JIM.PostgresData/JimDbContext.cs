// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.ExampleData;
using JIM.Models.Logic;
using JIM.Models.Preview;
using JIM.Models.Scheduling;
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
    public virtual DbSet<ActivityRunProfileExecutionItemSyncOutcome> ActivityRunProfileExecutionItemSyncOutcomes { get; set; } = null!;
    public virtual DbSet<CausalEdge> CausalEdges { get; set; } = null!;
    public virtual DbSet<ActivityStatCounter> ActivityStatCounters { get; set; } = null!;
    public virtual DbSet<ActivityPhase> ActivityPhases { get; set; } = null!;
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
    public virtual DbSet<ConnectedSystemObjectTypeTag> ConnectedSystemObjectTypeTags { get; set; } = null!;
    public virtual DbSet<ConnectedSystemPartition> ConnectedSystemPartitions { get; set; } = null!;
    public virtual DbSet<ConnectedSystemPasswordPolicy> ConnectedSystemPasswordPolicies { get; set; } = null!;
    public virtual DbSet<ConnectedSystemPasswordSynchronisation> ConnectedSystemPasswordSynchronisations { get; set; } = null!;
    public virtual DbSet<ConnectedSystemRunProfile> ConnectedSystemRunProfiles { get; set; } = null!;
    public virtual DbSet<ConnectedSystemSettingValue> ConnectedSystemSettingValues { get; set; } = null!;
    public virtual DbSet<ConnectorContainer> ConnectorContainers { get; set; } = null!;
    public virtual DbSet<ConnectorDefinition> ConnectorDefinitions { get; set; } = null!;
    public virtual DbSet<ConnectorDefinitionFile> ConnectorDefinitionFiles { get; set; } = null!;
    public virtual DbSet<ConnectorDefinitionSetting> ConnectorDefinitionSettings { get; set; } = null!;
    public virtual DbSet<ConnectorPartition> ConnectorPartitions { get; set; } = null!;
    public virtual DbSet<ConfigurationChangePreview> ConfigurationChangePreviews { get; set; } = null!;
    public virtual DbSet<ConfigurationChangePreviewGroup> ConfigurationChangePreviewGroups { get; set; } = null!;
    public virtual DbSet<ConfigurationChangePreviewDelta> ConfigurationChangePreviewDeltas { get; set; } = null!;
    public virtual DbSet<ExampleDataObjectType> ExampleDataObjectTypes { get; set; } = null!;
    public virtual DbSet<ExampleDataTemplate> ExampleDataTemplates { get; set; } = null!;
    public virtual DbSet<ExampleDataTemplateAttribute> ExampleDataTemplateAttributes { get; set; } = null!;
    public virtual DbSet<ExampleDataTemplateAttributeDependency> ExampleDataTemplateAttributeDependencies { get; set; } = null!;
    public virtual DbSet<ExampleDataTemplateAttributeWeightedValue> ExampleDataTemplateAttributeWeightedValues { get; set; } = null!;
    public virtual DbSet<ExampleDataTemplateWorkerTask> ExampleDataTemplateWorkerTasks { get; set; } = null!;
    public virtual DbSet<DeleteConnectedSystemWorkerTask> DeleteConnectedSystemWorkerTasks { get; set; } = null!;
    public virtual DbSet<ExampleDataSet> ExampleDataSets { get; set; } = null!;
    public virtual DbSet<ExampleDataSetInstance> ExampleDataSetInstances { get; set; } = null!;
    public virtual DbSet<ExampleDataSetValue> ExampleDataSetValues { get; set; } = null!;
    public virtual DbSet<MetaverseAttribute> MetaverseAttributes { get; set; } = null!;
    public virtual DbSet<MetaverseAttributeStandardMapping> MetaverseAttributeStandardMappings { get; set; } = null!;
    public virtual DbSet<MetaverseObject> MetaverseObjects { get; set; } = null!;
    public virtual DbSet<MetaverseObjectAttributeValue> MetaverseObjectAttributeValues { get; set; } = null!;
    public virtual DbSet<MetaverseObjectChange> MetaverseObjectChanges { get; set; } = null!;
    public virtual DbSet<MetaverseObjectChangeAttribute> MetaverseObjectChangeAttributes { get; set; } = null!;
    public virtual DbSet<MetaverseObjectChangeAttributeValue> MetaverseObjectChangeAttributeValues { get; set; } = null!;
    public virtual DbSet<MetaverseObjectType> MetaverseObjectTypes { get; set; } = null!;
    public virtual DbSet<DeferredReference> DeferredReferences { get; set; } = null!;
    public virtual DbSet<PendingExport> PendingExports { get; set; } = null!;
    public virtual DbSet<PendingInitialPassword> PendingInitialPasswords { get; set; } = null!;
    public virtual DbSet<PendingPasswordChange> PendingPasswordChanges { get; set; } = null!;
    public virtual DbSet<PendingExportAttributeValueChange> PendingExportAttributeValueChanges { get; set; } = null!;
    public virtual DbSet<PredefinedSearch> PredefinedSearches { get; set; } = null!;
    public virtual DbSet<PredefinedSearchAttribute> PredefinedSearchAttributes {  get; set; } = null!;
    public virtual DbSet<PredefinedSearchCriteria> PredefinedSearchCriteria { get; set; } = null!;
    public virtual DbSet<PredefinedSearchCriteriaGroup> PredefinedSearchCriteriaGroups { get; set; } = null!;
    public virtual DbSet<Role> Roles { get; set; } = null!;
    public virtual DbSet<Schedule> Schedules { get; set; } = null!;
    public virtual DbSet<ScheduleStep> ScheduleSteps { get; set; } = null!;
    public virtual DbSet<ScheduleExecution> ScheduleExecutions { get; set; } = null!;
    public virtual DbSet<ApiKey> ApiKeys { get; set; } = null!;
    public virtual DbSet<ServiceSettings> ServiceSettings { get; set; } = null!;
    public virtual DbSet<ServiceSetting> ServiceSettingItems { get; set; } = null!;
    public virtual DbSet<ObjectMatchingRule> ObjectMatchingRules { get; set; } = null!;
    public virtual DbSet<ObjectMatchingRuleSource> ObjectMatchingRuleSources { get; set; } = null!;
    public virtual DbSet<SyncRule> SyncRules { get; set; } = null!;
    public virtual DbSet<SyncRuleInitialPassword> SyncRuleInitialPasswords { get; set; } = null!;
    public virtual DbSet<SyncRuleMapping> SyncRuleMappings { get; set; } = null!;
    public virtual DbSet<SyncRuleMappingSource> SyncRuleMappingSources { get; set; } = null!;
    public virtual DbSet<SyncRuleScopingCriteria> SyncRuleScopingCriteria { get; set; } = null!;
    public virtual DbSet<SyncRuleScopingCriteriaGroup> SyncRuleScopingCriteriaGroups { get; set; } = null!;
    public virtual DbSet<SchemaRefreshRemovalWorkerTask> SchemaRefreshRemovalWorkerTasks { get; set; } = null!;
    public virtual DbSet<SynchronisationWorkerTask> SynchronisationWorkerTasks { get; set; } = null!;
    public virtual DbSet<PasswordDeliveryWorkerTask> PasswordDeliveryWorkerTasks { get; set; } = null!;
    public virtual DbSet<TemporalScopeReconciliationWorkerTask> TemporalScopeReconciliationWorkerTasks { get; set; } = null!;
    public virtual DbSet<HistoryRetentionCleanupWorkerTask> HistoryRetentionCleanupWorkerTasks { get; set; } = null!;
    public virtual DbSet<TrustedCertificate> TrustedCertificates { get; set; } = null!;
    public virtual DbSet<ConfigurationChangePreviewWorkerTask> ConfigurationChangePreviewWorkerTasks { get; set; } = null!;
    public virtual DbSet<WorkerTask> WorkerTasks { get; set; } = null!;

    // Connection pooling constants
    private const int MinimumPoolSize = 5;
    private const int MaximumPoolSize = 30;
    private const int ConnectionIdleLifetimeSeconds = 300;
    private const int ConnectionPruningIntervalSeconds = 30;

    private readonly string? _connectionString;

    /// <summary>
    /// Builds a standardised Npgsql connection string from environment variables.
    /// All JIM services should use this method to ensure uniform pool and timeout settings.
    /// </summary>
    /// <param name="commandTimeoutSeconds">Optional command timeout override (e.g. for bulk operations in the Worker).</param>
    public static string BuildConnectionString(int? commandTimeoutSeconds = null)
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

        // Connection pooling settings for optimal performance
        // - Minimum Pool Size: Keep connections warm to reduce latency for common operations
        // - Maximum Pool Size: Limit per-process connections (3 services × 30 = 90, leaving headroom within PostgreSQL's max_connections=200)
        // - Connection Idle Lifetime: Recycle idle connections after 5 minutes
        // - Connection Pruning Interval: Check for idle connections every 30 seconds
        var connectionString = $"Host={dbHostName};Database={dbName};Username={dbUsername};Password={dbPassword}" +
                               $";Minimum Pool Size={MinimumPoolSize};Maximum Pool Size={MaximumPoolSize}" +
                               $";Connection Idle Lifetime={ConnectionIdleLifetimeSeconds};Connection Pruning Interval={ConnectionPruningIntervalSeconds}";

        if (commandTimeoutSeconds.HasValue)
            connectionString += $";Command Timeout={commandTimeoutSeconds.Value}";

        _ = bool.TryParse(dbLogSensitiveInfo, out var logSensitiveInfo);
        if (logSensitiveInfo)
            connectionString += ";Include Error Detail=True";

        return connectionString;
    }

    /// <summary>
    /// Builds a connection string for a dedicated notification-listener connection (PostgreSQL LISTEN;
    /// issue #307). LISTEN requires a long-lived connection outside the pool, so pooling is disabled and
    /// TCP keepalives are enabled to detect dead connections promptly. At most one such connection exists
    /// per service, so this does not pressure the PostgreSQL connection limit.
    /// </summary>
    public static string BuildListenerConnectionString()
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

        return $"Host={dbHostName};Database={dbName};Username={dbUsername};Password={dbPassword}" +
               ";Pooling=false;Keepalive=30";
    }

    // Parameterless constructor for migrations and manual instantiation
    public JimDbContext()
    {
        _connectionString = BuildConnectionString();
    }

    // Constructor for dependency injection with DbContextOptions
    public JimDbContext(DbContextOptions<JimDbContext> options) : base(options)
    {
        // When using DI, options are already configured, so we don't need to build connection string
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        // Only configure if not already configured (i.e., when using parameterless constructor)
        if (!optionsBuilder.IsConfigured && _connectionString != null)
        {
            // Note: EnableRetryOnFailure is NOT configured here because the codebase has
            // manual transactions (BeginTransactionAsync) that are incompatible with
            // NpgsqlRetryingExecutionStrategy. Each transaction site must be wrapped in
            // CreateExecutionStrategy().ExecuteAsync() before retry can be enabled.
            // See issue #408 for the tracking item.
            // Transient failures are handled at the API level by GlobalExceptionHandler (HTTP 503).
            // Both suppressions below are load-bearing; neither is a leftover. Removing either
            // one has a specific, immediate consequence.
            //
            // PendingModelChangesWarning: JIM's runtime model permanently disagrees with its own
            // migrations, by exactly the 99 DateTime columns in the schema. PostgresDataRepository's
            // constructor sets the Npgsql.EnableLegacyTimestampBehavior AppContext switch, under
            // which DateTime maps to "timestamp without time zone". The EF tooling never constructs
            // that repository, so every migration and JimDbContextModelSnapshot.cs was scaffolded
            // with the switch off and declares "timestamp with time zone", which is also what the
            // database actually holds. Every service process therefore starts up carrying 99
            // AlterColumn differences, and MigrateAsync() throws on the first boot without this
            // line (verified by removing it: JIM.Worker fails InitialiseDatabaseAsync immediately).
            // The cost is that a genuine model change is invisible here too, so the checks that do
            // still bite are the design-time ones, which run with the switch off: the
            // 'dotnet ef migrations has-pending-model-changes' command, and
            // MigrationDesignerChainTests in JIM.Worker.Tests. Retiring this suppression means
            // retiring the legacy switch and normalising DateTime.Kind to Utc at every write; the
            // schema itself already needs no change.
            //
            // MultipleCollectionIncludeWarning: AsSplitQuery() was deliberately removed from the
            // sync paths because of the EF Core materialisation bug (dotnet/efcore#33826) that
            // silently drops navigation properties during concurrent writes. The remaining
            // single-query includes are intentional; the warning is the price of not reintroducing
            // a data integrity risk.
            optionsBuilder.UseNpgsql(_connectionString)
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .ConfigureWarnings(warnings => warnings.Ignore(
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning,
                    Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning));
        }
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
            .HasForeignKey(i => i.ConnectedSystemObjectId)
            .OnDelete(DeleteBehavior.SetNull); // let the db clear the fk value to the CSO.
        
        modelBuilder.Entity<ConnectedSystemObject>()
            .HasMany(cso => cso.Changes)
            .WithOne(c => c.ConnectedSystemObject)
            .OnDelete(DeleteBehavior.SetNull); // let the db clear the fk value to the CSO.

        // Activity stat counters (#1078): composite natural key so the incremental upsert has a
        // conflict target, cascading away with the owning Activity.
        modelBuilder.Entity<ActivityStatCounter>(entity =>
        {
            entity.HasKey(c => new { c.ActivityId, c.Dimension, c.Key });
            entity.HasOne<Activity>()
                .WithMany()
                .HasForeignKey(c => c.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // Activity phases (#454): the steps of a Run Profile execution, read in run order by the
        // portal, the API and PowerShell, and entered by key while the run progresses. Both access
        // patterns get an index, and the key is unique per Activity so entering a phase is
        // unambiguous. Cascades away with the owning Activity.
        modelBuilder.Entity<ActivityPhase>(entity =>
        {
            entity.HasKey(p => p.Id);
            entity.HasOne<Activity>()
                .WithMany()
                .HasForeignKey(p => p.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(p => new { p.ActivityId, p.Order });
            entity.HasIndex(p => new { p.ActivityId, p.Key }).IsUnique();
        });

        // The causal walk's degraded timeline key (#1495): after a record's deletion nulls
        // ConnectedSystemObjectId on the items that processed it, the source-import hop is found by the
        // external ID snapshot within the Activity's Connected System instead, and that lookup must not
        // scan a table this large.
        modelBuilder.Entity<ActivityRunProfileExecutionItem>()
            .HasIndex(rpei => rpei.ExternalIdSnapshot);

        // ActivityRunProfileExecutionItemSyncOutcome: cascade delete when parent RPEI is deleted
        modelBuilder.Entity<ActivityRunProfileExecutionItem>()
            .HasMany(rpei => rpei.SyncOutcomes)
            .WithOne(o => o.ActivityRunProfileExecutionItem)
            .HasForeignKey(o => o.ActivityRunProfileExecutionItemId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_SyncOutcomes_ActivityRunProfileExecutionItems");

        // Self-referential tree: set parent to null when parent outcome is deleted
        modelBuilder.Entity<ActivityRunProfileExecutionItemSyncOutcome>()
            .HasOne(o => o.ParentSyncOutcome)
            .WithMany(o => o.Children)
            .HasForeignKey(o => o.ParentSyncOutcomeId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("FK_SyncOutcomes_ParentSyncOutcome");

        // Optional FK to ConnectedSystemObjectChange for PendingExportCreated outcomes.
        // SetNull on delete: if the change record is cleaned up by retention, the outcome
        // node remains but loses its expandable attribute detail.
        modelBuilder.Entity<ActivityRunProfileExecutionItemSyncOutcome>()
            .HasOne(o => o.ConnectedSystemObjectChange)
            .WithMany()
            .HasForeignKey(o => o.ConnectedSystemObjectChangeId)
            .OnDelete(DeleteBehavior.SetNull)
            .HasConstraintName("FK_SyncOutcomes_ConnectedSystemObjectChange");

        // When an RPEI is deleted, set the FK to null on any change objects that reference it.
        // This preserves change history while allowing RPEI cleanup.
        modelBuilder.Entity<ConnectedSystemObjectChange>()
            .HasOne(c => c.ActivityRunProfileExecutionItem)
            .WithOne(r => r.ConnectedSystemObjectChange)
            .HasForeignKey<ConnectedSystemObjectChange>(c => c.ActivityRunProfileExecutionItemId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ConnectedSystemObjectChange>()
            .HasMany(cso => cso.AttributeChanges)
            .WithOne(ac => ac.ConnectedSystemChange)
            .OnDelete(DeleteBehavior.Cascade); // let the db delete all dependent ConnectedSystemObjectChangeAttribute objects when the parent is deleted.

        modelBuilder.Entity<ConnectedSystemObjectChangeAttribute>()
            .HasMany(ca => ca.ValueChanges)
            .WithOne(av => av.ConnectedSystemObjectChangeAttribute)
            .OnDelete(DeleteBehavior.Cascade); // let the db delete all dependent ConnectedSystemObjectChangeAttributeValue objects when the parent is deleted.

        // When a Connected System attribute definition is deleted, preserve the change history record
        // by setting the FK to null. The AttributeName and AttributeType sibling properties retain
        // the attribute metadata even after the definition is removed.
        modelBuilder.Entity<ConnectedSystemObjectChangeAttribute>()
            .HasOne(ca => ca.Attribute)
            .WithMany()
            .OnDelete(DeleteBehavior.SetNull);

        // When a CSO is deleted, set the ReferenceValueId to null in any change attribute values that reference it.
        // This prevents FK violations when deleting CSOs that are referenced in historical change records.
        modelBuilder.Entity<ConnectedSystemObjectChangeAttributeValue>()
            .HasOne(cav => cav.ReferenceValue)
            .WithMany()
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ConnectedSystemObjectType>()
            .HasMany(csot => csot.Attributes)
            .WithOne(csa => csa.ConnectedSystemObjectType);

        // The Object Type a Reference attribute declares as its target (#1285). Distinct from the owning
        // relationship above, so it is configured explicitly. SetNull: removing an Object Type must not take
        // attributes of other Object Types with it; the reference simply loses its declared target and
        // resolution falls back to searching every Object Type.
        modelBuilder.Entity<ConnectedSystemObjectTypeAttribute>()
            .HasOne(csa => csa.ReferencedObjectType)
            .WithMany()
            .HasForeignKey(csa => csa.ReferencedObjectTypeId)
            .OnDelete(DeleteBehavior.SetNull);

        // Classification tags have no meaning without the object type they classify, so they go with it. The unique
        // index enforces the same rule schema import applies in memory: a type is classified a given way once.
        modelBuilder.Entity<ConnectedSystemObjectType>()
            .HasMany(csot => csot.Tags)
            .WithOne(tag => tag.ConnectedSystemObjectType)
            .HasForeignKey(tag => tag.ConnectedSystemObjectTypeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ConnectedSystemObjectTypeTag>()
            .HasIndex(tag => new { tag.ConnectedSystemObjectTypeId, tag.Key, tag.Value })
            .IsUnique();

        modelBuilder.Entity<ConnectedSystemObjectTypeTag>()
            .Property(tag => tag.Key)
            .HasMaxLength(64);

        modelBuilder.Entity<ConnectedSystemObjectTypeTag>()
            .Property(tag => tag.Value)
            .HasMaxLength(256);

        // A Connected System has at most one discovered password policy. Every other child of a Connected System
        // is a collection, so this one-to-one has to be declared explicitly: EF cannot infer which end is the
        // dependent. Cascade, because a discovered policy has no meaning without the system it was read from.
        modelBuilder.Entity<ConnectedSystem>()
            .HasOne(cs => cs.PasswordPolicy)
            .WithOne(pp => pp.ConnectedSystem)
            .HasForeignKey<ConnectedSystemPasswordPolicy>(pp => pp.ConnectedSystemId)
            .OnDelete(DeleteBehavior.Cascade);

        // A Connected System has at most one Password Synchronisation configuration, declared explicitly for the
        // same reason as the policy above. Cascade: a system that no longer exists cannot receive passwords, and
        // the queued changes aimed at it cascade away with it.
        modelBuilder.Entity<ConnectedSystem>()
            .HasOne(cs => cs.PasswordSynchronisation)
            .WithOne()
            .HasForeignKey<ConnectedSystemPasswordSynchronisation>(ps => ps.ConnectedSystemId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict rather than cascade: deleting the Object Type that receives passwords must not silently delete
        // the configuration naming it and leave the system quietly not synchronising. The delete fails, and the
        // administrator repoints or removes the configuration deliberately.
        // Declared without a navigation on either end: the Object Type is already reachable through the
        // Connected System, and a navigation here would close a cycle the OpenAPI schema generator cannot
        // collapse (see ConnectedSystemPasswordSynchronisation.TargetObjectTypeId).
        modelBuilder.Entity<ConnectedSystemPasswordSynchronisation>()
            .HasOne<ConnectedSystemObjectType>()
            .WithMany()
            .HasForeignKey(ps => ps.TargetObjectTypeId)
            .OnDelete(DeleteBehavior.Restrict);

        // Answers "which systems are enabled for Password Synchronisation?", which fan-out asks on every password
        // change, without loading the Connected Systems themselves.
        modelBuilder.Entity<ConnectedSystemPasswordSynchronisation>()
            .HasIndex(ps => ps.Enabled)
            .HasDatabaseName("IX_ConnectedSystemPasswordSynchronisations_Enabled");

        modelBuilder.Entity<MetaverseObject>()
            .HasMany(mo => mo.Roles)
            .WithMany(r => r.StaticMembers);

        // ApiKey to Role many-to-many relationship
        modelBuilder.Entity<ApiKey>()
            .HasMany(ak => ak.Roles)
            .WithMany();

        modelBuilder.Entity<MetaverseObject>()
            .HasMany(mvo => mvo.Changes)
            .WithOne(mvoc => mvoc.MetaverseObject);

        // When a metaverse attribute definition is deleted, preserve the change history record
        // by setting the FK to null. The AttributeName and AttributeType sibling properties retain
        // the attribute metadata even after the definition is removed.
        modelBuilder.Entity<MetaverseObjectChangeAttribute>()
            .HasOne(ca => ca.Attribute)
            .WithMany()
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<MetaverseObjectAttributeValue>()
            .HasOne(moav => moav.MetaverseObject)
            .WithMany(mo => mo.AttributeValues);

        // Attribute priority provenance (#91): the Synchronisation Rule whose mapping won resolution and
        // contributed this value. SetNull on rule deletion so the denormalised ContributedBySystemId record
        // survives; ContributedBySyncRuleId then reads null (provenance no longer resolvable to a live rule).
        modelBuilder.Entity<MetaverseObjectAttributeValue>()
            .HasOne(moav => moav.ContributedBySyncRule)
            .WithMany()
            .HasForeignKey(moav => moav.ContributedBySyncRuleId)
            .OnDelete(DeleteBehavior.SetNull);

        // Asserted-null marker (#91): false by default. The store-level default backfills existing rows so they
        // remain ordinary value rows, never asserted nulls.
        modelBuilder.Entity<MetaverseObjectAttributeValue>()
            .Property(moav => moav.NullValue)
            .HasDefaultValue(false);

        modelBuilder.Entity<MetaverseObjectAttributeValue>()
            .HasOne(moav => moav.ReferenceValue)
            .WithMany();
        
        modelBuilder.Entity<MetaverseObjectAttributeValue>()
            .HasOne(moav => moav.UnresolvedReferenceValue)
            .WithMany();

        modelBuilder.Entity<MetaverseObjectType>()
            .HasMany(mot => mot.Attributes);

        // Authoritative source trigger mode (#119). The store-level default backfills existing rows with
        // SpecificSourcesDisconnect (0), the behaviour they were configured with before trigger modes
        // existed (#115), so the migration is behaviour-preserving with no backfill. New entities read the
        // property initialiser instead and start at the safe default (AllSourcesDisconnect).
        modelBuilder.Entity<MetaverseObjectType>()
            .Property(mot => mot.DeletionTriggerMode)
            .HasDefaultValue(AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect);

        // advisory Standard Mapping metadata (#1104). Mappings are owned by their attribute (cascade delete),
        // and each (attribute, standard, counterpart name) combination exists at most once so the built-in
        // schema synchronisation pass converges rather than duplicates.
        modelBuilder.Entity<MetaverseAttribute>()
            .HasMany(a => a.StandardMappings)
            .WithOne(m => m.MetaverseAttribute)
            .HasForeignKey(m => m.MetaverseAttributeId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<MetaverseAttributeStandardMapping>()
            .HasIndex(m => new { m.MetaverseAttributeId, m.Standard, m.CounterpartName })
            .IsUnique()
            .HasDatabaseName("IX_MetaverseAttributeStandardMappings_Attribute_Standard_Name");

        modelBuilder.Entity<SyncRule>()
            .HasMany(sr => sr.AttributeFlowRules)
            .WithOne(afr => afr.SyncRule);

        // Inbound value processing defaults to TreatWhitespaceAsNoValue (JIM's opinionated default).
        // The store-level default backfills existing rows on migration so the whitespace-as-no-value
        // behaviour applies to mappings created before this feature shipped (#843).
        modelBuilder.Entity<SyncRuleMapping>()
            .Property(srm => srm.InboundValueProcessing)
            .HasDefaultValue(InboundValueProcessing.TreatWhitespaceAsNoValue);

        // Attribute priority (#91). Priority defaults to int.MaxValue (the safe-addition sentinel) so existing
        // import mappings, and any newly added one, never win resolution until an admin explicitly orders the
        // attribute's priority list. NullIsValue defaults to false (fallback behaviour). The store-level defaults
        // backfill existing rows on migration.
        modelBuilder.Entity<SyncRuleMapping>()
            .Property(srm => srm.Priority)
            .HasDefaultValue(int.MaxValue);

        modelBuilder.Entity<SyncRuleMapping>()
            .Property(srm => srm.NullIsValue)
            .HasDefaultValue(false);

        // Initial Export Only (#223). Defaults to false (the attribute is fully managed); the store-level
        // default backfills existing rows on migration.
        modelBuilder.Entity<SyncRuleMapping>()
            .Property(srm => srm.InitialExportOnly)
            .HasDefaultValue(false);

        // Per-mapping enable/disable (#1485). Defaults to true so every mapping persisted before this field
        // existed keeps flowing exactly as it always has; the store-level default backfills existing rows.
        modelBuilder.Entity<SyncRuleMapping>()
            .Property(srm => srm.Enabled)
            .HasDefaultValue(true);

        // SPEC-1082 D10: Run Profile Verification Mode defaults to false (no behavioural change for
        // existing Run Profiles); the store-level default backfills existing rows on migration.
        modelBuilder.Entity<ConnectedSystemRunProfile>()
            .Property(rp => rp.VerifyImportContentHashes)
            .HasDefaultValue(false);

        // ObjectMatchingRule can belong to either SyncRule or ConnectedSystemObjectType (mutually exclusive)
        modelBuilder.Entity<SyncRule>()
            .HasMany(sr => sr.ObjectMatchingRules)
            .WithOne(omr => omr.SyncRule)
            .HasForeignKey(omr => omr.SyncRuleId);

        modelBuilder.Entity<ConnectedSystemObjectType>()
            .HasMany(csot => csot.ObjectMatchingRules)
            .WithOne(omr => omr.ConnectedSystemObjectType)
            .HasForeignKey(omr => omr.ConnectedSystemObjectTypeId);

        modelBuilder.Entity<ObjectMatchingRule>()
            .HasOne(omr => omr.MetaverseObjectType)
            .WithMany()
            .HasForeignKey(omr => omr.MetaverseObjectTypeId)
            .OnDelete(DeleteBehavior.SetNull);

        modelBuilder.Entity<ObjectMatchingRule>()
            .HasMany(omr => omr.Sources)
            .WithOne(s => s.ObjectMatchingRule)
            .HasForeignKey(s => s.ObjectMatchingRuleId)
            .OnDelete(DeleteBehavior.Cascade);

        // reduce the chance of concurrency issues by using a system attribute to identify row versions
        // for our most heavily updated objects.
        // https://www.npgsql.org/efcore/modeling/concurrency.html?tabs=data-annotations
        // https://learn.microsoft.com/en-us/ef/core/saving/concurrency?tabs=data-annotations
        // Note: In Npgsql.EntityFrameworkCore.PostgreSQL 7.0+, UseXminAsConcurrencyToken() is obsolete.
        // Use the standard EF Core approach with a uint xmin property and IsRowVersion() instead.
        modelBuilder.Entity<MetaverseObject>()
            .Property(e => e.xmin)
            .IsRowVersion();

        // PendingExport: relationship to source MVO (Q1 decision)
        modelBuilder.Entity<PendingExport>()
            .HasOne(pe => pe.SourceMetaverseObject)
            .WithMany()
            .HasForeignKey(pe => pe.SourceMetaverseObjectId)
            .OnDelete(DeleteBehavior.SetNull);

        // PendingExport: relationship to Connected System Object
        // Explicit FK configuration ensures the property is used instead of a shadow property
        modelBuilder.Entity<PendingExport>()
            .HasOne(pe => pe.ConnectedSystemObject)
            .WithMany()
            .HasForeignKey(pe => pe.ConnectedSystemObjectId)
            .OnDelete(DeleteBehavior.SetNull);

        // PendingExport: the Synchronisation Rule whose provisioning decision produced a Create.
        // SetNull rather than Cascade: deleting a rule must not delete exports already staged for accounts it
        // provisioned. Losing the link simply means the account does not get an initial password, which is the
        // right outcome once the rule that asked for one is gone.
        modelBuilder.Entity<PendingExport>()
            .HasOne(pe => pe.ProvisioningSyncRule)
            .WithMany()
            .HasForeignKey(pe => pe.ProvisioningSyncRuleId)
            .OnDelete(DeleteBehavior.SetNull);

        // Partial, because the column is set only on a provisioning Create and null on every update and delete,
        // which at customer scale is the overwhelming majority of a table that is written in bulk on every
        // export evaluation. The index exists to keep deleting a Synchronisation Rule from scanning the whole
        // table to null the column out, and rows that are already null are not rows that delete has to visit.
        modelBuilder.Entity<PendingExport>()
            .HasIndex(pe => pe.ProvisioningSyncRuleId)
            .HasDatabaseName("IX_PendingExports_ProvisioningSyncRuleId")
            .HasFilter("\"ProvisioningSyncRuleId\" IS NOT NULL");

        // A Synchronisation Rule has at most one initial-password configuration. Cascade, because the
        // configuration has no meaning without the rule that provisions with it. The generator settings live in
        // the same table as owned columns: they have no identity of their own and are never queried apart from
        // the configuration that holds them.
        modelBuilder.Entity<SyncRule>()
            .HasOne(sr => sr.InitialPassword)
            .WithOne(ip => ip.SyncRule)
            .HasForeignKey<SyncRuleInitialPassword>(ip => ip.SyncRuleId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<SyncRuleInitialPassword>()
            .OwnsOne(ip => ip.CustomPolicy);

        // DeferredReference: relationships for reference resolution
        modelBuilder.Entity<DeferredReference>()
            .HasOne(dr => dr.SourceCso)
            .WithMany()
            .HasForeignKey(dr => dr.SourceCsoId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DeferredReference>()
            .HasOne(dr => dr.TargetMvo)
            .WithMany()
            .HasForeignKey(dr => dr.TargetMvoId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<DeferredReference>()
            .HasOne(dr => dr.TargetSystem)
            .WithMany()
            .HasForeignKey(dr => dr.TargetSystemId)
            .OnDelete(DeleteBehavior.Cascade);

        // Index for efficient deferred reference lookup
        modelBuilder.Entity<DeferredReference>()
            .HasIndex(dr => new { dr.TargetMvoId, dr.TargetSystemId });

        // TrustedCertificate: unique index on Thumbprint to prevent duplicates
        modelBuilder.Entity<TrustedCertificate>()
            .HasIndex(tc => tc.Thumbprint)
            .IsUnique();

        // Performance indexes for frequently queried tables
        // ConnectedSystemObject: composite index for lookups by system and type
        modelBuilder.Entity<ConnectedSystemObject>()
            .HasIndex(cso => new { cso.ConnectedSystemId, cso.TypeId })
            .HasDatabaseName("IX_ConnectedSystemObjects_ConnectedSystemId_TypeId");

        // PendingExport: composite index for export queries by system and status
        modelBuilder.Entity<PendingExport>()
            .HasIndex(pe => new { pe.ConnectedSystemId, pe.Status })
            .HasDatabaseName("IX_PendingExports_ConnectedSystemId_Status");

        // PendingExport: partial index for the deferred-reference second pass (#1102).
        // Rows with unresolved references are rare (usually zero), so the partial index
        // keeps the common no-deferred-exports probe near-free at any scale.
        modelBuilder.Entity<PendingExport>()
            .HasIndex(pe => pe.ConnectedSystemId)
            .HasDatabaseName("IX_PendingExports_ConnectedSystemId_HasUnresolvedReferences")
            .HasFilter("\"HasUnresolvedReferences\"");

        // PendingExport: composite index supporting keyset pagination in export batch collection
        // (ORDER BY CreatedAt, Id with a (CreatedAt, Id) > (cursor) predicate; issue #985).
        modelBuilder.Entity<PendingExport>()
            .HasIndex(pe => new { pe.ConnectedSystemId, pe.CreatedAt, pe.Id })
            .HasDatabaseName("IX_PendingExports_ConnectedSystemId_CreatedAt_Id");

        // PendingExport: filtered unique index to prevent duplicate Pending Exports for the same CSO.
        // PendingInitialPassword: the account it is owed to. Cascade, because an account that no longer exists
        // cannot be owed a password.
        modelBuilder.Entity<PendingInitialPassword>()
            .HasOne(pip => pip.ConnectedSystemObject)
            .WithMany()
            .HasForeignKey(pip => pip.ConnectedSystemObjectId)
            .OnDelete(DeleteBehavior.Cascade);

        // The Synchronisation Rule whose configuration governs the delivery. SetNull rather than Cascade:
        // deleting a rule must not erase the record that an account is still waiting, which is a fact about the
        // account rather than about the rule. Without a rule there is no configuration to generate from, so the
        // delivery cannot proceed, and that is a thing an administrator needs to be able to see.
        modelBuilder.Entity<PendingInitialPassword>()
            .HasOne(pip => pip.SyncRule)
            .WithMany()
            .HasForeignKey(pip => pip.SyncRuleId)
            .OnDelete(DeleteBehavior.SetNull);

        // One outstanding initial password per account. A second would mean two deliveries racing to set a
        // password on the same object, with the later one silently winning.
        modelBuilder.Entity<PendingInitialPassword>()
            .HasIndex(pip => pip.ConnectedSystemObjectId)
            .IsUnique()
            .HasDatabaseName("IX_PendingInitialPasswords_ConnectedSystemObjectId_Unique");

        // What the worker asks for on every export run, and what the portal's needs-attention indicators ask
        // for on every page load: what is outstanding on this Connected System, in this state.
        modelBuilder.Entity<PendingInitialPassword>()
            .HasIndex(pip => new { pip.ConnectedSystemId, pip.Status })
            .HasDatabaseName("IX_PendingInitialPasswords_ConnectedSystemId_Status");

        // The Password Synchronisation queue (#1119). Foreign keys with no navigations on either end, following
        // ConnectedSystemPasswordSynchronisation: nothing needs to walk from a queue row back to the identity or
        // the system, and a navigation would close a schema cycle the OpenAPI document build cannot collapse.
        modelBuilder.Entity<PendingPasswordChange>()
            .HasOne<MetaverseObject>()
            .WithMany()
            .HasForeignKey(ppc => ppc.MetaverseObjectId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<PendingPasswordChange>()
            .HasOne<ConnectedSystem>()
            .WithMany()
            .HasForeignKey(ppc => ppc.ConnectedSystemId)
            .OnDelete(DeleteBehavior.Cascade);

        // Set null rather than cascade: an account being deleted and recreated must not take the password change
        // with it. The change re-resolves its account on the next attempt, which is the same path a change queued
        // before the account existed takes.
        modelBuilder.Entity<PendingPasswordChange>()
            .HasOne<ConnectedSystemObject>()
            .WithMany()
            .HasForeignKey(ppc => ppc.ConnectedSystemObjectId)
            .OnDelete(DeleteBehavior.SetNull);

        // Requirement 8's coalescing, enforced by the database rather than by the code that writes it. The
        // fan-out UPSERTs on this key, so two near-simultaneous password changes for one identity cannot both
        // insert: the second updates the first in place, and last-write-wins is atomic. Application-side
        // read-modify-write would leave that race open.
        modelBuilder.Entity<PendingPasswordChange>()
            .HasIndex(ppc => new { ppc.MetaverseObjectId, ppc.ConnectedSystemId })
            .IsUnique()
            .HasDatabaseName("IX_PendingPasswordChanges_MetaverseObjectId_ConnectedSystemId_Unique");

        // What the delivery pass asks for: the changes owed to this system, in this state, that have come due.
        modelBuilder.Entity<PendingPasswordChange>()
            .HasIndex(ppc => new { ppc.ConnectedSystemId, ppc.Status, ppc.NextRetryAt })
            .HasDatabaseName("IX_PendingPasswordChanges_ConnectedSystemId_Status_NextRetryAt");

        // What the Metaverse Object's password panel asks for: everything outstanding for this identity.
        modelBuilder.Entity<PendingPasswordChange>()
            .HasIndex(ppc => ppc.MetaverseObjectId)
            .HasDatabaseName("IX_PendingPasswordChanges_MetaverseObjectId");

        // Only one Pending Export should exist per CSO at any time. The filter excludes rows where
        // ConnectedSystemObjectId is NULL (e.g., PEs for unresolved references not yet matched to a CSO).
        modelBuilder.Entity<PendingExport>()
            .HasIndex(pe => pe.ConnectedSystemObjectId)
            .IsUnique()
            .HasFilter(@"""ConnectedSystemObjectId"" IS NOT NULL")
            .HasDatabaseName("IX_PendingExports_ConnectedSystemObjectId_Unique");

        // MetaverseObjectAttributeValue: index for attribute lookups by value
        modelBuilder.Entity<MetaverseObjectAttributeValue>()
            .HasIndex(moav => new { moav.AttributeId, moav.StringValue })
            .HasDatabaseName("IX_MetaverseObjectAttributeValues_AttributeId_StringValue");

        // MetaverseObjectAttributeValue: composite index for MVO attribute lookups
        // Mirrors IX_ConnectedSystemObjectAttributeValues_CsoId_AttributeId for CSOs.
        // Accelerates: sort-by-attribute subqueries, criteria filter EXISTS subqueries,
        // and attribute value bulk fetches in GetMetaverseObjectHeadersPagedAsync.
        modelBuilder.Entity<MetaverseObjectAttributeValue>()
            .HasIndex("MetaverseObjectId", "AttributeId")
            .HasDatabaseName("IX_MetaverseObjectAttributeValues_MvoId_AttributeId");

        // ConnectedSystemObjectAttributeValue: composite index for CSO attribute lookups
        // Uses shadow property "ConnectedSystemObjectId" created by EF convention
        modelBuilder.Entity<ConnectedSystemObjectAttributeValue>()
            .HasIndex("ConnectedSystemObjectId", "AttributeId")
            .HasDatabaseName("IX_ConnectedSystemObjectAttributeValues_CsoId_AttributeId");

        // Partial index on UnresolvedReferenceValue — only indexes rows where the value is set.
        // Keeps the index small and allows the cross-batch reference fixup UPDATE to locate
        // unresolved rows efficiently without scanning the entire table.
        modelBuilder.Entity<ConnectedSystemObjectAttributeValue>()
            .HasIndex(av => av.UnresolvedReferenceValue)
            .HasDatabaseName("IX_ConnectedSystemObjectAttributeValues_UnresolvedReferenceValue")
            .HasFilter("\"UnresolvedReferenceValue\" IS NOT NULL");

        // Composite index on (AttributeId, StringValue) — speeds up the target-side join in the
        // fixup query where UnresolvedReferenceValue is matched against the secondary external ID
        // attribute value (StringValue) for a known AttributeId.
        modelBuilder.Entity<ConnectedSystemObjectAttributeValue>()
            .HasIndex(av => new { av.AttributeId, av.StringValue })
            .HasDatabaseName("IX_ConnectedSystemObjectAttributeValues_AttributeId_StringValue")
            .HasFilter("\"StringValue\" IS NOT NULL");

        // Composite index on (AttributeId, DateTimeValue) — the Temporal Scope Reconciler's candidate
        // pre-filter (issue #892) selects CSOs whose date attribute value falls in a boundary-crossing
        // range for a known AttributeId, e.g. WHERE AttributeId = @dateAttr AND DateTimeValue >= @lo
        // AND DateTimeValue < @hi. Partial (DateTimeValue IS NOT NULL) to keep it small.
        modelBuilder.Entity<ConnectedSystemObjectAttributeValue>()
            .HasIndex(av => new { av.AttributeId, av.DateTimeValue })
            .HasDatabaseName("IX_ConnectedSystemObjectAttributeValues_AttributeId_DateTimeValue")
            .HasFilter("\"DateTimeValue\" IS NOT NULL");

        // Composite index on (AttributeId, DateTimeValue) for the outbound (MVO export) lane of the
        // Temporal Scope Reconciler (issue #892); mirrors the CSO index above. Supersedes the bare
        // [Index(DateTimeValue)] on the entity for this equality-then-range access pattern.
        modelBuilder.Entity<MetaverseObjectAttributeValue>()
            .HasIndex(mav => new { mav.AttributeId, mav.DateTimeValue })
            .HasDatabaseName("IX_MetaverseObjectAttributeValues_AttributeId_DateTimeValue")
            .HasFilter("\"DateTimeValue\" IS NOT NULL");

        // Partial index on the Temporal Scope Reconciler flag (issue #892). The sync engine drains flagged
        // Metaverse Objects into export re-evaluation each run (WHERE "ScopeReviewPending" = true); flags are
        // rare (O(transitions)), so a partial index keeps that scan O(flagged) rather than O(all MVOs).
        modelBuilder.Entity<MetaverseObject>()
            .HasIndex(mvo => mvo.ScopeReviewPending)
            .HasDatabaseName("IX_MetaverseObjects_ScopeReviewPending")
            .HasFilter("\"ScopeReviewPending\"");

        // Delta sync performance: composite index for timestamp-based queries
        // These enable efficient filtering by ConnectedSystemId + LastUpdated/Created
        // which is used in GetConnectedSystemObjectsModifiedSinceAsync
        modelBuilder.Entity<ConnectedSystemObject>()
            .HasIndex(cso => new { cso.ConnectedSystemId, cso.LastUpdated })
            .HasDatabaseName("IX_ConnectedSystemObjects_ConnectedSystemId_LastUpdated");

        modelBuilder.Entity<ConnectedSystemObject>()
            .HasIndex(cso => new { cso.ConnectedSystemId, cso.Created })
            .HasDatabaseName("IX_ConnectedSystemObjects_ConnectedSystemId_Created");

        // ConnectedSystemObject: filtered unique index backing the "at most one CSO per (Connected System,
        // Metaverse Object)" join invariant. The sync engine's EstablishJoinAsync already enforces this at the
        // application layer; this index backs the same invariant at the database level so no non-engine write
        // path (raw SQL, a future connector, manual data fixes) can join a second CSO in the same Connected
        // System to the same Metaverse Object. The filter excludes unjoined CSOs (MetaverseObjectId IS NULL),
        // which must not collide with each other.
        modelBuilder.Entity<ConnectedSystemObject>()
            .HasIndex(cso => new { cso.ConnectedSystemId, cso.MetaverseObjectId })
            .IsUnique()
            .HasFilter("\"MetaverseObjectId\" IS NOT NULL")
            .HasDatabaseName("IX_ConnectedSystemObjects_ConnectedSystemId_MetaverseObjectId_Unique");

        // Additional performance indexes for worker task queue processing
        // Optimises GetNextWorkerTaskAsync and GetNextWorkerTasksToProcessAsync queries
        modelBuilder.Entity<WorkerTask>()
            .HasIndex(wt => new { wt.Status, wt.Timestamp })
            .HasDatabaseName("IX_WorkerTasks_Status_Timestamp");

        // Performance index for activity audit trail queries
        // Optimises timestamp-based activity lookups
        modelBuilder.Entity<Activity>()
            .HasIndex(a => a.Created)
            .HasDatabaseName("IX_Activities_Created")
            .IsDescending(true);

        // Performance index for Metaverse Object deletion (issue #993): every MVO delete nulls
        // Activities.MetaverseObjectId to preserve audit history, and without this index that
        // UPDATE sequentially scans the Activities table once per deletion batch.
        modelBuilder.Entity<Activity>()
            .HasIndex(a => a.MetaverseObjectId)
            .HasDatabaseName("IX_Activities_MetaverseObjectId");

        // Security audit events (issue #500): aggregated failed-authentication rows are upserted by matching on
        // (TargetType, ApiKeyPrefix, ClientIpAddress, SecurityEventReason, AggregationWindowStart). The partial
        // unique index (scoped to rows that actually carry a window, via HasFilter) makes the increment-or-insert
        // upsert in SecurityAuditServer race-safe: a concurrent insert for the same window bucket hits this
        // constraint instead of creating a duplicate row. ApiKeyPrefix/ClientIpAddress are normalised to "" rather
        // than left null for aggregated rows specifically because Postgres unique indexes treat NULLs as distinct
        // from one another, which would defeat deduplication for the bad-format failure path (no key prefix
        // available); see Activity.ApiKeyPrefix and Activity.ClientIpAddress for the normalisation contract.
        modelBuilder.Entity<Activity>()
            .HasIndex(a => new { a.TargetType, a.ApiKeyPrefix, a.ClientIpAddress, a.SecurityEventReason, a.AggregationWindowStart })
            .IsUnique()
            .HasFilter("\"AggregationWindowStart\" IS NOT NULL")
            .HasDatabaseName("IX_Activities_SecurityAggregation_Unique");

        // Supports the security-event retention cleanup (TargetType == Authentication && Created < cutoff) and the
        // Activities list/API filter by target type over a time range.
        modelBuilder.Entity<Activity>()
            .HasIndex(a => new { a.TargetType, a.Created })
            .HasDatabaseName("IX_Activities_TargetType_Created");

        // Schedule attribution on Activities (issue #1196). The Operations History Schedule filter narrows on the
        // denormalised ScheduledByScheduleId, and the Schedule Execution drill-downs select on ScheduleExecutionId,
        // which carried no index at all; without both, either query sequential-scans the whole Activities table.
        modelBuilder.Entity<Activity>()
            .HasIndex(a => a.ScheduledByScheduleId)
            .HasDatabaseName("IX_Activities_ScheduledByScheduleId");

        modelBuilder.Entity<Activity>()
            .HasIndex(a => a.ScheduleExecutionId)
            .HasDatabaseName("IX_Activities_ScheduleExecutionId");

        // Sync outcome indexes for RPEI detail loading and aggregate stats queries
        modelBuilder.Entity<ActivityRunProfileExecutionItemSyncOutcome>()
            .HasIndex(o => o.ActivityRunProfileExecutionItemId)
            .HasDatabaseName("IX_ActivityRunProfileExecutionItemSyncOutcomes_ActivityRunProfileExecutionItemId");

        modelBuilder.Entity<ActivityRunProfileExecutionItemSyncOutcome>()
            .HasIndex(o => new { o.ActivityRunProfileExecutionItemId, o.OutcomeType })
            .HasDatabaseName("IX_ActivityRunProfileExecutionItemSyncOutcomes_RpeiId_OutcomeType");

        // Causal provenance (#1223). The effect side cascades from the Run Profile Execution Item, so purging an
        // Activity takes its edges with it; the cause side is deliberately unconstrained scalars so purging a cause
        // leaves intact the edge that records it was once the cause. See the CausalEdge type remarks.
        modelBuilder.Entity<CausalEdge>()
            .HasOne(e => e.EffectRunProfileExecutionItem)
            .WithMany()
            .HasForeignKey(e => e.EffectRunProfileExecutionItemId)
            .OnDelete(DeleteBehavior.Cascade)
            .HasConstraintName("FK_CausalEdges_ActivityRunProfileExecutionItems");

        // Traversal runs in both directions (upward in Phase 1, downward in Phase 2), so both ends are indexed;
        // an index that serves one direction and table-scans the other fails half of what this feature is for.
        modelBuilder.Entity<CausalEdge>()
            .HasIndex(e => e.EffectRunProfileExecutionItemId)
            .HasDatabaseName("IX_CausalEdges_EffectRunProfileExecutionItemId");

        modelBuilder.Entity<CausalEdge>()
            .HasIndex(e => e.CauseRunProfileExecutionItemId)
            .HasDatabaseName("IX_CausalEdges_CauseRunProfileExecutionItemId");

        modelBuilder.Entity<CausalEdge>()
            .HasIndex(e => e.CauseMetaverseObjectId)
            .HasDatabaseName("IX_CausalEdges_CauseMetaverseObjectId");

        // Configuration change preview (#827). All three tables hang off the preview's Activity and cascade from it,
        // so the existing history-retention housekeeping removes preview results with the Activity that owns them;
        // no separate cleanup, and no way for preview data (which holds attribute values) to outlive its retention.
        modelBuilder.Entity<ConfigurationChangePreview>(preview =>
        {
            // The Activity id IS the preview's key: a preview and its Activity are one thing, and sharing the key
            // makes the 1:1 unenforceable-by-accident rather than merely conventional.
            preview.HasKey(p => p.ActivityId);

            preview.HasOne(p => p.Activity)
                .WithOne()
                .HasForeignKey<ConfigurationChangePreview>(p => p.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConfigurationChangePreviewGroup>(group =>
        {
            group.HasOne(g => g.Preview)
                .WithMany(p => p.Groups)
                .HasForeignKey(g => g.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);

            // The summary landing view reads every group for one preview, ordered by size.
            group.HasIndex(g => g.ActivityId)
                .HasDatabaseName("IX_ConfigurationChangePreviewGroups_ActivityId");
        });

        modelBuilder.Entity<ConfigurationChangePreviewDelta>(delta =>
        {
            delta.HasOne(d => d.Preview)
                .WithMany(p => p.Deltas)
                .HasForeignKey(d => d.ActivityId)
                .OnDelete(DeleteBehavior.Cascade);

            delta.HasOne(d => d.Group)
                .WithMany(g => g.Deltas)
                .HasForeignKey(d => d.GroupId)
                .OnDelete(DeleteBehavior.Cascade);

            // Drill-down is always "the rows of one group of one preview", server-side paginated.
            delta.HasIndex(d => new { d.ActivityId, d.GroupId })
                .HasDatabaseName("IX_ConfigurationChangePreviewDeltas_ActivityId_GroupId");

            // Filtering a preview by transition type without picking a group ("show me everything that would be
            // disconnected") is the other access path, and it must not degrade into a scan of every delta row.
            delta.HasIndex(d => new { d.ActivityId, d.TransitionType })
                .HasDatabaseName("IX_ConfigurationChangePreviewDeltas_ActivityId_TransitionType");
        });

        // Performance index for Metaverse Object deletion automation
        // Optimises GetMetaverseObjectsEligibleForDeletionAsync queries
        // Uses string-based column name to reference the shadow foreign key property "TypeId"
        modelBuilder.Entity<MetaverseObject>()
            .HasIndex(new[] { nameof(MetaverseObject.Origin), "TypeId", nameof(MetaverseObject.LastConnectorDisconnectedDate) })
            .HasDatabaseName("IX_MetaverseObjects_Origin_Type_LastDisconnected");

        // Performance index for Metaverse Object Type lookups
        // Optimises name-based type lookups with deletion rule filtering
        modelBuilder.Entity<MetaverseObjectType>()
            .HasIndex(mot => new { mot.Name, mot.DeletionRule })
            .HasDatabaseName("IX_MetaverseObjectTypes_Name_DeletionRule");

        // Note: Indexes on foreign key columns (ConnectedSystemId, SourceCsoId) are automatically created by Npgsql,
        // so we don't need explicit HasIndex() definitions for those.

        // ---------------------------------------------------------------------------------------------------------
        // Scheduling entities
        // ---------------------------------------------------------------------------------------------------------

        // Schedule -> ScheduleStep relationship (cascade delete steps when schedule is deleted)
        modelBuilder.Entity<Schedule>()
            .HasMany(s => s.Steps)
            .WithOne(st => st.Schedule)
            .HasForeignKey(st => st.ScheduleId)
            .OnDelete(DeleteBehavior.Cascade);

        // Schedule -> ScheduleExecution relationship (cascade delete executions when schedule is deleted)
        modelBuilder.Entity<Schedule>()
            .HasMany(s => s.Executions)
            .WithOne(e => e.Schedule)
            .HasForeignKey(e => e.ScheduleId)
            .OnDelete(DeleteBehavior.Cascade);

        // WorkerTask -> ScheduleExecution relationship (set null when execution is deleted)
        modelBuilder.Entity<WorkerTask>()
            .HasOne(wt => wt.ScheduleExecution)
            .WithMany()
            .HasForeignKey(wt => wt.ScheduleExecutionId)
            .OnDelete(DeleteBehavior.SetNull);

        // Index for schedule name uniqueness (optional but useful)
        modelBuilder.Entity<Schedule>()
            .HasIndex(s => s.Name)
            .IsUnique()
            .HasDatabaseName("IX_Schedules_Name");

        // Index for finding due schedules efficiently
        modelBuilder.Entity<Schedule>()
            .HasIndex(s => new { s.IsEnabled, s.NextRunTime })
            .HasDatabaseName("IX_Schedules_IsEnabled_NextRunTime");

        // Index for schedule step ordering
        modelBuilder.Entity<ScheduleStep>()
            .HasIndex(st => new { st.ScheduleId, st.StepIndex })
            .HasDatabaseName("IX_ScheduleSteps_ScheduleId_StepIndex");

        // Index for active executions lookup
        modelBuilder.Entity<ScheduleExecution>()
            .HasIndex(se => new { se.Status, se.QueuedAt })
            .HasDatabaseName("IX_ScheduleExecutions_Status_QueuedAt");

        // Index for a Schedule's most recent execution. The Schedules list projects each Schedule's last execution
        // via a correlated "order by QueuedAt descending, take one" subquery; this composite index makes each of
        // those an index-backed LIMIT 1 rather than a sort over every execution the Schedule has ever had.
        modelBuilder.Entity<ScheduleExecution>()
            .HasIndex(se => new { se.ScheduleId, se.QueuedAt })
            .HasDatabaseName("IX_ScheduleExecutions_ScheduleId_QueuedAt");

        // Index for worker tasks by schedule execution
        modelBuilder.Entity<WorkerTask>()
            .HasIndex(wt => wt.ScheduleExecutionId)
            .HasDatabaseName("IX_WorkerTasks_ScheduleExecutionId");

        // ---------------------------------------------------------------------------------------------------------
        // Configuration ownership (issue #1477)
        // ---------------------------------------------------------------------------------------------------------
        // Each relationship below is containment: the child has no meaning once its owner is gone. They were all
        // left to convention, and because every one of these foreign keys is optional, the convention is
        // ClientSetNull, which becomes NO ACTION in the database. That is wrong twice over. It orphans child rows
        // whenever the owner is deleted outside a change-tracked graph, and it makes the factory reset's
        // "DELETE ... WHERE ""BuiltIn"" = false" statements fail with 23503 for any custom object holding the child
        // rows it ordinarily holds; since the whole wipe is one transaction, the reset then rolls back entirely.
        // DeletePathForeignKeyCoverageTests asserts this property across the whole schema, so a child table added
        // later cannot silently reintroduce the fault.

        // A Predefined Search owns its top-level criteria groups; a group is how the search filters.
        modelBuilder.Entity<PredefinedSearch>()
            .HasMany(ps => ps.CriteriaGroups)
            .WithOne()
            .HasForeignKey(g => g.PredefinedSearchId)
            .OnDelete(DeleteBehavior.Cascade);

        // A criteria group owns its nested groups. Without this the cascade above stops at the top level and a
        // nested group holds the whole delete up.
        modelBuilder.Entity<PredefinedSearchCriteriaGroup>()
            .HasMany(g => g.ChildGroups)
            .WithOne(g => g.ParentGroup)
            .HasForeignKey(g => g.ParentGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // A criteria group owns its criteria.
        modelBuilder.Entity<PredefinedSearchCriteriaGroup>()
            .HasMany(g => g.Criteria)
            .WithOne()
            .HasForeignKey(c => c.PredefinedSearchCriteriaGroupId)
            .OnDelete(DeleteBehavior.Cascade);

        // A Container owns the Containers discovered beneath it. Exactly the nested-group case above, and it bit
        // the same way: deleting a Connected System removes its Containers with one statement keyed on PartitionId,
        // but a Container discovered below another carries no PartitionId of its own, so that statement deleted
        // the top of each branch and left every descendant pointing at a row that had just gone. PostgreSQL
        // refused on this foreign key and the whole delete rolled back, so a Connected System that had ever
        // imported a nested hierarchy could not be deleted at all.
        modelBuilder.Entity<ConnectedSystemContainer>()
            .HasMany(c => c.ChildContainers)
            .WithOne(c => c.ParentContainer)
            .HasForeignKey(c => c.ParentContainerId)
            .OnDelete(DeleteBehavior.Cascade);

        // A Connector Definition owns the settings it declares.
        modelBuilder.Entity<ConnectorDefinition>()
            .HasMany(cd => cd.Settings)
            .WithOne()
            .OnDelete(DeleteBehavior.Cascade);

        // An Example Data Set owns its values; the set is nothing but its values.
        modelBuilder.Entity<ExampleDataSet>()
            .HasMany(ds => ds.Values)
            .WithOne()
            .OnDelete(DeleteBehavior.Cascade);

        // An Example Data Template owns the Object Types it covers, each of which owns the attributes it
        // generates, each of which owns its weighted values. The whole chain has to cascade: stopping part way
        // down leaves the delete blocked one level deeper instead of at the top.
        modelBuilder.Entity<ExampleDataTemplate>()
            .HasMany(t => t.ObjectTypes)
            .WithOne()
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ExampleDataObjectType>()
            .HasMany(ot => ot.TemplateAttributes)
            .WithOne()
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<ExampleDataTemplateAttribute>()
            .HasMany(ta => ta.WeightedStringValues)
            .WithOne()
            .OnDelete(DeleteBehavior.Cascade);
    }
}