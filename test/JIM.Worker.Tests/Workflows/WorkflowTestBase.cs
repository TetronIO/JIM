using JIM.Application;
using JIM.Data;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Base class for workflow tests that need a real database context and JIM application.
/// Workflow tests sit between unit tests (mocked dependencies) and integration tests (full Docker stack).
/// They test multi-step business processes using real implementations with an in-memory database.
/// </summary>
public abstract class WorkflowTestBase
{
    protected JimDbContext DbContext = null!;
    protected IRepository Repository = null!;
    protected JimApplication Jim = null!;

    [SetUp]
    public void BaseSetUp()
    {
        TestUtilities.SetEnvironmentVariables();

        // Create in-memory database for fast, isolated tests
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()) // Unique DB per test
            .EnableSensitiveDataLogging()
            .Options;

        DbContext = new JimDbContext(options);
        Repository = new PostgresRepository(DbContext);
        Jim = new JimApplication(Repository);
    }

    [TearDown]
    public void BaseTearDown()
    {
        DbContext?.Dispose();
    }

    #region Helper Methods for Test Setup

    /// <summary>
    /// Creates a Connected System with basic configuration.
    /// </summary>
    protected async Task<ConnectedSystem> CreateConnectedSystemWithSyncRulesAsync(string name)
    {
        var connectedSystem = new ConnectedSystem
        {
            Name = name,
            Description = "Test system for workflow tests",
            ConnectorType = ConnectorType.CSV,
            Enabled = true,
            Created = DateTime.UtcNow,
            LastDeltaSyncCompletedAt = null // Start with no watermark
        };

        await Jim.Repository.ConnectedSystems.CreateConnectedSystemAsync(connectedSystem);
        return connectedSystem;
    }

    /// <summary>
    /// Creates a Connected System Object Type (e.g., "User").
    /// </summary>
    protected async Task<ConnectedSystemObjectType> CreateCsoTypeAsync(int connectedSystemId, string name)
    {
        var csoType = new ConnectedSystemObjectType
        {
            ConnectedSystemId = connectedSystemId,
            Name = name,
            RemoveContributedAttributesOnObsoletion = true,
            Attributes = new List<ConnectedSystemAttribute>()
        };

        await Jim.Repository.ConnectedSystems.CreateConnectedSystemObjectTypeAsync(csoType);
        return csoType;
    }

    /// <summary>
    /// Creates a Metaverse Object Type (e.g., "Person").
    /// </summary>
    protected async Task<MetaverseObjectType> CreateMvObjectTypeAsync(string name)
    {
        var mvType = new MetaverseObjectType
        {
            Name = name,
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            DeletionGracePeriodDays = 30,
            Attributes = new List<MetaverseAttribute>()
        };

        await Jim.Repository.Metaverse.CreateMetaverseObjectTypeAsync(mvType);
        return mvType;
    }

    /// <summary>
    /// Creates an import sync rule (CS → MV).
    /// </summary>
    protected async Task<SyncRule> CreateImportSyncRuleAsync(
        int connectedSystemId,
        int csoTypeId,
        int mvTypeId,
        bool projectToMetaverse = false)
    {
        var syncRule = new SyncRule
        {
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemObjectTypeId = csoTypeId,
            MetaverseObjectTypeId = mvTypeId,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            Precedence = 100,
            ProjectToMetaverse = projectToMetaverse,
            InboundOutOfScopeAction = InboundOutOfScopeAction.Disconnect,
            AttributeFlowRules = new List<SyncRuleMapping>(),
            ObjectMatchingRules = new List<ObjectMatchingRule>(),
            ObjectScopingCriteriaGroups = new List<ObjectScopingCriteriaGroup>()
        };

        await Jim.Repository.ConnectedSystems.CreateSyncRuleAsync(syncRule);
        return syncRule;
    }

    /// <summary>
    /// Creates a Connected System Object (CSO).
    /// </summary>
    protected async Task<ConnectedSystemObject> CreateCsoAsync(int connectedSystemId, int typeId, string objectId)
    {
        var cso = new ConnectedSystemObject
        {
            ConnectedSystemId = connectedSystemId,
            TypeId = typeId,
            ObjectId = objectId,
            Status = ConnectedSystemObjectStatus.Normal,
            Created = DateTime.UtcNow,
            LastUpdated = null, // Typically set by import processor
            AttributeValues = new List<ConnectedSystemObjectAttributeValue>()
        };

        await Jim.Repository.ConnectedSystems.CreateConnectedSystemObjectAsync(cso);
        return cso;
    }

    /// <summary>
    /// Creates an Activity for a run profile execution.
    /// </summary>
    protected async Task<Activity> CreateActivityAsync(int connectedSystemId, ConnectedSystemRunType runType)
    {
        var activity = new Activity
        {
            ConnectedSystemId = connectedSystemId,
            ConnectedSystemRunType = runType,
            Status = ActivityStatus.InProgress,
            StartTime = DateTime.UtcNow,
            ObjectsToProcess = 0,
            ObjectsProcessed = 0,
            RunProfileExecutionItems = new List<ActivityRunProfileExecutionItem>()
        };

        await Jim.Repository.Activity.CreateActivityAsync(activity);
        return activity;
    }

    /// <summary>
    /// Creates a Run Profile.
    /// </summary>
    protected async Task<ConnectedSystemRunProfile> CreateRunProfileAsync(int connectedSystemId, ConnectedSystemRunType runType)
    {
        var runProfile = new ConnectedSystemRunProfile
        {
            ConnectedSystemId = connectedSystemId,
            Name = $"{runType} Profile",
            RunType = runType,
            Enabled = true,
            PageSize = 200
        };

        await Jim.Repository.ConnectedSystems.CreateConnectedSystemRunProfileAsync(runProfile);
        return runProfile;
    }

    /// <summary>
    /// Reloads an entity from the database to get updated values.
    /// Useful after a processor updates properties like LastDeltaSyncCompletedAt.
    /// </summary>
    protected async Task ReloadEntityAsync<T>(T entity) where T : class
    {
        var entry = DbContext.Entry(entity);
        await entry.ReloadAsync();
    }

    #endregion
}
