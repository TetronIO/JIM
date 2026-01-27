using JIM.Application;
using JIM.Connectors.Mock;
using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Tasking;
using JIM.Models.Transactional;
using JIM.PostgresData;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using Serilog;

namespace JIM.Workflow.Tests.Harness;

/// <summary>
/// Orchestrates multi-step workflow tests with state snapshots between steps.
/// Provides a high-level API for testing complete sync cycles (import → sync → export → confirming import).
/// </summary>
public class WorkflowTestHarness : IDisposable
{
    private readonly JimDbContext _dbContext;
    private readonly IRepository _repository;
    private readonly JimApplication _jim;
    private readonly Dictionary<string, ConnectedSystem> _connectedSystems = new();
    private readonly Dictionary<string, MockCallConnector> _connectors = new();
    private readonly Dictionary<string, ConnectedSystemObjectType> _objectTypes = new();
    private readonly List<WorkflowStateSnapshot> _snapshots = new();

    public JimDbContext DbContext => _dbContext;
    public IRepository Repository => _repository;
    public JimApplication Jim => _jim;

    /// <summary>
    /// All snapshots taken during this test.
    /// </summary>
    public IReadOnlyList<WorkflowStateSnapshot> Snapshots => _snapshots;

    /// <summary>
    /// The most recent snapshot.
    /// </summary>
    public WorkflowStateSnapshot? LatestSnapshot => _snapshots.Count > 0 ? _snapshots[^1] : null;

    public WorkflowTestHarness()
    {
        // Set environment variables needed by JIM
        Environment.SetEnvironmentVariable("JIM_DB_HOSTNAME", "localhost");
        Environment.SetEnvironmentVariable("JIM_DB_NAME", "jim_test");
        Environment.SetEnvironmentVariable("JIM_DB_USERNAME", "test");
        Environment.SetEnvironmentVariable("JIM_DB_PASSWORD", "test");

        // Create in-memory database for fast, isolated tests
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: $"WorkflowTest_{Guid.NewGuid()}")
            .EnableSensitiveDataLogging()
            .Options;

        _dbContext = new JimDbContext(options);
        _repository = new PostgresDataRepository(_dbContext);
        _jim = new JimApplication(_repository);
    }

    #region Setup Methods

    /// <summary>
    /// Creates a connected system with a mock connector.
    /// </summary>
    public async Task<ConnectedSystem> CreateConnectedSystemAsync(
        string name,
        Action<MockCallConnector>? configureConnector = null)
    {
        var connector = new MockCallConnector();
        configureConnector?.Invoke(connector);

        var connectedSystem = new ConnectedSystem
        {
            Name = name,
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };

        _dbContext.ConnectedSystems.Add(connectedSystem);
        await _dbContext.SaveChangesAsync();

        _connectedSystems[name] = connectedSystem;
        _connectors[name] = connector;

        return connectedSystem;
    }

    /// <summary>
    /// Gets the mock connector for a connected system.
    /// </summary>
    public MockCallConnector GetConnector(string systemName)
    {
        if (!_connectors.TryGetValue(systemName, out var connector))
            throw new InvalidOperationException($"No connector found for system '{systemName}'");
        return connector;
    }

    /// <summary>
    /// Gets a connected system by name.
    /// </summary>
    public ConnectedSystem GetConnectedSystem(string systemName)
    {
        if (!_connectedSystems.TryGetValue(systemName, out var system))
            throw new InvalidOperationException($"No connected system found with name '{systemName}'");
        return system;
    }

    /// <summary>
    /// Creates an object type (schema) for a connected system.
    /// </summary>
    public async Task<ConnectedSystemObjectType> CreateObjectTypeAsync(
        string systemName,
        string typeName,
        Action<ObjectTypeBuilder>? configure = null)
    {
        var system = GetConnectedSystem(systemName);

        var builder = new ObjectTypeBuilder(typeName);
        configure?.Invoke(builder);

        var objectType = builder.Build();
        objectType.ConnectedSystemId = system.Id;

        _dbContext.ConnectedSystemObjectTypes.Add(objectType);
        await _dbContext.SaveChangesAsync();

        var key = $"{systemName}:{typeName}";
        _objectTypes[key] = objectType;

        return objectType;
    }

    /// <summary>
    /// Gets an object type by system and type name.
    /// </summary>
    public ConnectedSystemObjectType GetObjectType(string systemName, string typeName)
    {
        var key = $"{systemName}:{typeName}";
        if (!_objectTypes.TryGetValue(key, out var objectType))
            throw new InvalidOperationException($"No object type found: {key}");
        return objectType;
    }

    /// <summary>
    /// Creates a metaverse object type.
    /// </summary>
    public async Task<MetaverseObjectType> CreateMetaverseObjectTypeAsync(
        string name,
        Action<MetaverseObjectTypeBuilder>? configure = null)
    {
        var builder = new MetaverseObjectTypeBuilder(name);
        configure?.Invoke(builder);

        var mvType = builder.Build();

        _dbContext.MetaverseObjectTypes.Add(mvType);
        await _dbContext.SaveChangesAsync();

        // Add attributes separately
        foreach (var attr in builder.GetAttributes())
        {
            attr.MetaverseObjectTypes = new List<MetaverseObjectType> { mvType };
            _dbContext.MetaverseAttributes.Add(attr);
        }
        await _dbContext.SaveChangesAsync();

        return mvType;
    }

    /// <summary>
    /// Creates a sync rule.
    /// </summary>
    public async Task<SyncRule> CreateSyncRuleAsync(
        string name,
        string systemName,
        string csoTypeName,
        MetaverseObjectType mvType,
        SyncRuleDirection direction,
        Action<SyncRuleBuilder>? configure = null)
    {
        var system = GetConnectedSystem(systemName);
        var csoType = GetObjectType(systemName, csoTypeName);

        var builder = new SyncRuleBuilder(name, direction);
        configure?.Invoke(builder);

        var syncRule = builder.Build();
        syncRule.ConnectedSystemId = system.Id;
        syncRule.ConnectedSystem = system;
        syncRule.ConnectedSystemObjectTypeId = csoType.Id;
        syncRule.ConnectedSystemObjectType = csoType;
        syncRule.MetaverseObjectTypeId = mvType.Id;
        syncRule.MetaverseObjectType = mvType;

        _dbContext.SyncRules.Add(syncRule);
        await _dbContext.SaveChangesAsync();

        return syncRule;
    }

    /// <summary>
    /// Creates a run profile for a connected system.
    /// </summary>
    public async Task<ConnectedSystemRunProfile> CreateRunProfileAsync(
        string systemName,
        string profileName,
        ConnectedSystemRunType runType)
    {
        var system = GetConnectedSystem(systemName);

        var runProfile = new ConnectedSystemRunProfile
        {
            ConnectedSystemId = system.Id,
            Name = profileName,
            RunType = runType
        };

        _dbContext.ConnectedSystemRunProfiles.Add(runProfile);
        await _dbContext.SaveChangesAsync();

        return runProfile;
    }

    #endregion

    #region Workflow Step Methods

    /// <summary>
    /// Executes a full import from a connected system.
    /// </summary>
    public async Task<Activity> ExecuteFullImportAsync(string systemName)
    {
        var system = GetConnectedSystem(systemName);
        var connector = GetConnector(systemName);

        var runProfile = await CreateRunProfileAsync(systemName, "Full Import", ConnectedSystemRunType.FullImport);
        var activity = await CreateActivityAsync(system.Id, runProfile, ConnectedSystemRunType.FullImport);
        var workerTask = CreateWorkerTask(activity);

        var cts = new CancellationTokenSource();
        var processor = new SyncImportTaskProcessor(
            _jim,
            connector,
            system,
            runProfile,
            workerTask,
            cts);

        await processor.PerformFullImportAsync();

        return await ReloadEntityAsync(activity);
    }

    /// <summary>
    /// Executes a full sync for a connected system.
    /// </summary>
    public async Task<Activity> ExecuteFullSyncAsync(string systemName)
    {
        var system = GetConnectedSystem(systemName);

        var runProfile = await CreateRunProfileAsync(systemName, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(system.Id, runProfile, ConnectedSystemRunType.FullSynchronisation);

        var cts = new CancellationTokenSource();
        var processor = new SyncFullSyncTaskProcessor(
            _jim,
            system,
            runProfile,
            activity,
            cts);

        await processor.PerformFullSyncAsync();

        return await ReloadEntityAsync(activity);
    }

    /// <summary>
    /// Executes export evaluation for all MVOs that need exporting.
    /// This creates PendingExports based on export sync rules.
    /// </summary>
    public async Task ExecuteExportEvaluationAsync(string sourceSystemName)
    {
        var sourceSystem = GetConnectedSystem(sourceSystemName);

        // Get all MVOs and evaluate export rules for each
        var mvos = await _dbContext.MetaverseObjects
            .Include(m => m.Type)
            .Include(m => m.AttributeValues)
                .ThenInclude(av => av.Attribute)
            .ToListAsync();

        // Build export evaluation cache
        var cache = await _jim.ExportEvaluation.BuildExportEvaluationCacheAsync(sourceSystem.Id);

        foreach (var mvo in mvos)
        {
            // For initial export evaluation, treat all attributes as "changed"
            var allAttributes = mvo.AttributeValues.ToList();
            await _jim.ExportEvaluation.EvaluateExportRulesAsync(mvo, allAttributes, sourceSystem, cache);
        }
    }

    /// <summary>
    /// Executes pending exports to a target system.
    /// </summary>
    public async Task<Activity> ExecuteExportAsync(string targetSystemName)
    {
        var system = GetConnectedSystem(targetSystemName);
        var connector = GetConnector(targetSystemName);

        var runProfile = await CreateRunProfileAsync(targetSystemName, "Export", ConnectedSystemRunType.Export);
        var activity = await CreateActivityAsync(system.Id, runProfile, ConnectedSystemRunType.Export);

        await _jim.ExportExecution.ExecuteExportsAsync(system, connector, SyncRunMode.PreviewAndSync);

        return await ReloadEntityAsync(activity);
    }

    /// <summary>
    /// Executes a confirming import from the target system.
    /// This verifies that exports were successfully applied.
    /// </summary>
    public async Task<Activity> ExecuteConfirmingImportAsync(string systemName)
    {
        // For confirming import, we run a full import which will:
        // 1. Find PendingProvisioning CSOs by secondary external ID
        // 2. Transition them to Normal status
        // 3. Reconcile pending exports
        return await ExecuteFullImportAsync(systemName);
    }

    /// <summary>
    /// Executes a delta sync for a connected system.
    /// Used for incremental sync operations including drift detection.
    /// </summary>
    public async Task<Activity> ExecuteDeltaSyncAsync(string systemName)
    {
        var system = GetConnectedSystem(systemName);

        var runProfile = await CreateRunProfileAsync(systemName, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var activity = await CreateActivityAsync(system.Id, runProfile, ConnectedSystemRunType.DeltaSynchronisation);

        var cts = new CancellationTokenSource();
        var processor = new SyncDeltaSyncTaskProcessor(
            _jim,
            system,
            runProfile,
            activity,
            cts);

        await processor.PerformDeltaSyncAsync();

        return await ReloadEntityAsync(activity);
    }

    #endregion

    #region Snapshot Methods

    /// <summary>
    /// Takes a snapshot of the current database state.
    /// </summary>
    public async Task<WorkflowStateSnapshot> TakeSnapshotAsync(string stepName)
    {
        var snapshot = await WorkflowStateSnapshot.CaptureAsync(_dbContext, stepName);
        _snapshots.Add(snapshot);
        return snapshot;
    }

    /// <summary>
    /// Gets a snapshot by index.
    /// </summary>
    public WorkflowStateSnapshot GetSnapshot(int index)
    {
        if (index < 0 || index >= _snapshots.Count)
            throw new ArgumentOutOfRangeException(nameof(index));
        return _snapshots[index];
    }

    /// <summary>
    /// Gets a snapshot by step name.
    /// </summary>
    public WorkflowStateSnapshot? GetSnapshot(string stepName)
    {
        return _snapshots.FirstOrDefault(s => s.StepName == stepName);
    }

    /// <summary>
    /// Prints all snapshot summaries to the console.
    /// Useful for debugging test failures.
    /// </summary>
    public void PrintSnapshotSummaries()
    {
        foreach (var snapshot in _snapshots)
        {
            Console.WriteLine(snapshot.ToSummary());
            Console.WriteLine();
        }
    }

    #endregion

    #region Helper Methods

    private async Task<Activity> CreateActivityAsync(
        int connectedSystemId,
        ConnectedSystemRunProfile runProfile,
        ConnectedSystemRunType runType)
    {
        var activity = new Activity
        {
            TargetName = $"{runProfile.Name} Execution",
            Status = ActivityStatus.InProgress,
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemRunProfileId = runProfile.Id,
            ConnectedSystemRunType = runType
        };

        _dbContext.Activities.Add(activity);
        await _dbContext.SaveChangesAsync();

        return activity;
    }

    private async Task<T> ReloadEntityAsync<T>(T entity) where T : class
    {
        _dbContext.Entry(entity).State = EntityState.Detached;

        var idProperty = typeof(T).GetProperty("Id");
        if (idProperty != null)
        {
            var id = idProperty.GetValue(entity);

            // Special handling for Activity to include RunProfileExecutionItems
            if (typeof(T) == typeof(Activity) && id is Guid activityId)
            {
                var activity = await _dbContext.Set<Activity>()
                    .Include(a => a.RunProfileExecutionItems)
                    .FirstOrDefaultAsync(a => a.Id == activityId);
                return (activity as T) ?? entity;
            }

            var reloaded = await _dbContext.Set<T>().FindAsync(id);
            return reloaded ?? entity;
        }

        return entity;
    }

    private SynchronisationWorkerTask CreateWorkerTask(Activity activity)
    {
        return new SynchronisationWorkerTask
        {
            Id = Guid.NewGuid(),
            Activity = activity,
            InitiatedByType = ActivityInitiatorType.NotSet,
            InitiatedByName = "Workflow Test"
        };
    }

    public void Dispose()
    {
        _jim?.Dispose();
        (_repository as IDisposable)?.Dispose();
        _dbContext?.Dispose();
    }

    #endregion
}

#region Builder Classes

/// <summary>
/// Builder for creating ConnectedSystemObjectType with attributes.
/// </summary>
public class ObjectTypeBuilder
{
    private readonly string _name;
    private readonly List<ConnectedSystemObjectTypeAttribute> _attributes = new();

    public ObjectTypeBuilder(string name)
    {
        _name = name;
    }

    public ObjectTypeBuilder WithAttribute(string name, AttributeDataType type, bool isExternalId = false, bool isSecondaryExternalId = false)
    {
        _attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = name,
            Type = type,
            IsExternalId = isExternalId,
            IsSecondaryExternalId = isSecondaryExternalId,
            Selected = true
        });
        return this;
    }

    public ObjectTypeBuilder WithGuidExternalId(string name = "objectGUID")
    {
        return WithAttribute(name, AttributeDataType.Guid, isExternalId: true);
    }

    public ObjectTypeBuilder WithStringExternalId(string name)
    {
        return WithAttribute(name, AttributeDataType.Text, isExternalId: true);
    }

    public ObjectTypeBuilder WithStringSecondaryExternalId(string name = "distinguishedName")
    {
        return WithAttribute(name, AttributeDataType.Text, isSecondaryExternalId: true);
    }

    public ObjectTypeBuilder WithStringAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.Text);
    }

    public ObjectTypeBuilder WithIntAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.Number);
    }

    public ObjectTypeBuilder WithLongAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.LongNumber);
    }

    public ObjectTypeBuilder WithBoolAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.Boolean);
    }

    public ObjectTypeBuilder WithDateTimeAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.DateTime);
    }

    public ObjectTypeBuilder WithBinaryAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.Binary);
    }

    public ObjectTypeBuilder WithGuidAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.Guid);
    }

    public ObjectTypeBuilder WithReferenceAttribute(string name, bool isMultiValued = false)
    {
        _attributes.Add(new ConnectedSystemObjectTypeAttribute
        {
            Name = name,
            Type = AttributeDataType.Reference,
            IsExternalId = false,
            IsSecondaryExternalId = false,
            Selected = true,
            AttributePlurality = isMultiValued ? AttributePlurality.MultiValued : AttributePlurality.SingleValued
        });
        return this;
    }

    public ConnectedSystemObjectType Build()
    {
        return new ConnectedSystemObjectType
        {
            Name = _name,
            Selected = true,
            Attributes = _attributes
        };
    }
}

/// <summary>
/// Builder for creating MetaverseObjectType with attributes.
/// </summary>
public class MetaverseObjectTypeBuilder
{
    private readonly string _name;
    private readonly List<MetaverseAttribute> _attributes = new();

    public MetaverseObjectTypeBuilder(string name)
    {
        _name = name;
    }

    public MetaverseObjectTypeBuilder WithAttribute(string name, AttributeDataType type, AttributePlurality plurality = AttributePlurality.SingleValued)
    {
        _attributes.Add(new MetaverseAttribute
        {
            Name = name,
            Type = type,
            AttributePlurality = plurality,
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        });
        return this;
    }

    public MetaverseObjectTypeBuilder WithStringAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.Text);
    }

    public MetaverseObjectTypeBuilder WithIntAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.Number);
    }

    public MetaverseObjectTypeBuilder WithLongAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.LongNumber);
    }

    public MetaverseObjectTypeBuilder WithGuidAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.Guid);
    }

    public MetaverseObjectTypeBuilder WithBoolAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.Boolean);
    }

    public MetaverseObjectTypeBuilder WithDateTimeAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.DateTime);
    }

    public MetaverseObjectTypeBuilder WithBinaryAttribute(string name)
    {
        return WithAttribute(name, AttributeDataType.Binary);
    }

    public MetaverseObjectTypeBuilder WithReferenceAttribute(string name, bool isMultiValued = false)
    {
        return WithAttribute(name, AttributeDataType.Reference, isMultiValued ? AttributePlurality.MultiValued : AttributePlurality.SingleValued);
    }

    public List<MetaverseAttribute> GetAttributes() => _attributes;

    public MetaverseObjectType Build()
    {
        return new MetaverseObjectType
        {
            Name = _name,
            PluralName = _name + "s",
            BuiltIn = false,
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            Attributes = new List<MetaverseAttribute>(),
            DataGenerationTemplateAttributes = new List<JIM.Models.DataGeneration.DataGenerationTemplateAttribute>(),
            PredefinedSearches = new List<JIM.Models.Search.PredefinedSearch>(),
            DeletionTriggerConnectedSystemIds = new List<int>()
        };
    }
}

/// <summary>
/// Builder for creating SyncRules.
/// </summary>
public class SyncRuleBuilder
{
    private readonly string _name;
    private readonly SyncRuleDirection _direction;
    private bool _enabled = true;
    private bool _projectToMetaverse = true;
    private bool _provisionToConnectedSystem = false;
    private bool _enforceState = true;
    private readonly List<SyncRuleMapping> _attributeFlows = new();
    private readonly List<SyncRuleScopingCriteriaGroup> _scopingConditions = new();

    public SyncRuleBuilder(string name, SyncRuleDirection direction)
    {
        _name = name;
        _direction = direction;
    }

    public SyncRuleBuilder Enabled(bool enabled = true)
    {
        _enabled = enabled;
        return this;
    }

    public SyncRuleBuilder WithProjection(bool project = true)
    {
        _projectToMetaverse = project;
        return this;
    }

    public SyncRuleBuilder WithProvisioning(bool provision = true)
    {
        _provisionToConnectedSystem = provision;
        return this;
    }

    public SyncRuleBuilder WithEnforceState(bool enforceState = true)
    {
        _enforceState = enforceState;
        return this;
    }

    public SyncRuleBuilder WithAttributeFlow(MetaverseAttribute mvAttr, ConnectedSystemObjectTypeAttribute csAttr)
    {
        var flow = new SyncRuleMapping();

        var source = new SyncRuleMappingSource
        {
            MetaverseAttribute = _direction == SyncRuleDirection.Import ? null : mvAttr,
            MetaverseAttributeId = _direction == SyncRuleDirection.Import ? null : mvAttr.Id,
            ConnectedSystemAttribute = _direction == SyncRuleDirection.Import ? csAttr : null,
            ConnectedSystemAttributeId = _direction == SyncRuleDirection.Import ? csAttr.Id : null
        };
        flow.Sources.Add(source);

        if (_direction == SyncRuleDirection.Import)
        {
            flow.TargetMetaverseAttribute = mvAttr;
            flow.TargetMetaverseAttributeId = mvAttr.Id;
        }
        else
        {
            flow.TargetConnectedSystemAttribute = csAttr;
            flow.TargetConnectedSystemAttributeId = csAttr.Id;
        }

        _attributeFlows.Add(flow);
        return this;
    }

    public SyncRuleBuilder WithExpressionFlow(string expression, ConnectedSystemObjectTypeAttribute targetCsAttr)
    {
        var flow = new SyncRuleMapping();

        var source = new SyncRuleMappingSource
        {
            Expression = expression
        };
        flow.Sources.Add(source);

        flow.TargetConnectedSystemAttribute = targetCsAttr;
        flow.TargetConnectedSystemAttributeId = targetCsAttr.Id;

        _attributeFlows.Add(flow);
        return this;
    }

    public SyncRule Build()
    {
        return new SyncRule
        {
            Name = _name,
            Direction = _direction,
            Enabled = _enabled,
            EnforceState = _enforceState,
            ProjectToMetaverse = _projectToMetaverse,
            ProvisionToConnectedSystem = _provisionToConnectedSystem,
            AttributeFlowRules = _attributeFlows,
            ObjectScopingCriteriaGroups = _scopingConditions
        };
    }
}

#endregion
