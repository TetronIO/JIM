using JIM.Application;
using JIM.Data;
using JIM.Data.Repositories;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Base class for workflow tests that test multi-step processes using real implementations
/// with an in-memory database. These tests verify that components work correctly together,
/// catching integration bugs that unit tests with mocks would miss.
///
/// Example workflow: Full Sync → Delta Sync → verify only modified CSOs processed
/// </summary>
public abstract class WorkflowTestBase
{
    protected JimDbContext DbContext = null!;
    protected IRepository Repository = null!;
    protected JimApplication Jim = null!;
    protected JIM.InMemoryData.SyncRepository SyncRepo = null!;

    [SetUp]
    public void BaseSetUp()
    {
        // Set environment variables needed by JIM (even though they won't be used with in-memory DB)
        TestUtilities.SetEnvironmentVariables();

        // Create in-memory database for fast, isolated tests
        // Each test gets a unique database to ensure isolation
        var options = new DbContextOptionsBuilder<JimDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .EnableSensitiveDataLogging()
            .Options;

        DbContext = new JimDbContext(options);
        Repository = new PostgresDataRepository(DbContext);
        SyncRepo = new JIM.InMemoryData.SyncRepository();
        SyncRepo.SetSyncOutcomeTrackingLevel(
            JIM.Models.Activities.ActivityRunProfileExecutionItemSyncOutcomeTrackingLevel.Detailed);
        Jim = new JimApplication(Repository, syncRepository: SyncRepo);

        // Seed required service settings for sync processors
        SeedServiceSettingsAsync().GetAwaiter().GetResult();
    }

    /// <summary>
    /// Seeds the database with required service settings for sync processors.
    /// </summary>
    private async Task SeedServiceSettingsAsync()
    {
        DbContext.ServiceSettingItems.Add(new ServiceSetting
        {
            Key = "Sync.PageSize",
            DisplayName = "Sync Page Size",
            Category = ServiceSettingCategory.Synchronisation,
            ValueType = ServiceSettingValueType.Integer,
            DefaultValue = "1000",
            Value = null
        });

        DbContext.ServiceSettingItems.Add(new ServiceSetting
        {
            Key = "ChangeTracking.CsoChanges.Enabled",
            DisplayName = "Track CSO changes",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            Value = null
        });

        DbContext.ServiceSettingItems.Add(new ServiceSetting
        {
            Key = "ChangeTracking.MvoChanges.Enabled",
            DisplayName = "Track MVO changes",
            Category = ServiceSettingCategory.History,
            ValueType = ServiceSettingValueType.Boolean,
            DefaultValue = "true",
            Value = null
        });

        await DbContext.SaveChangesAsync();
    }

    [TearDown]
    public void BaseTearDown()
    {
        Jim?.Dispose();
        (Repository as IDisposable)?.Dispose();
        DbContext?.Dispose();
    }

    #region Helper Methods - Connected Systems

    /// <summary>
    /// Creates a Connected System with a basic schema (one object type with attributes).
    /// </summary>
    protected async Task<ConnectedSystem> CreateConnectedSystemAsync(string name)
    {
        var connectedSystem = new ConnectedSystem
        {
            Name = name,
            ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule
        };

        DbContext.ConnectedSystems.Add(connectedSystem);
        await DbContext.SaveChangesAsync();

        SyncRepo.SeedConnectedSystem(connectedSystem);

        return connectedSystem;
    }

    /// <summary>
    /// Creates a CSO type (schema definition) for a Connected System.
    /// </summary>
    protected async Task<ConnectedSystemObjectType> CreateCsoTypeAsync(
        int connectedSystemId,
        string name,
        List<ConnectedSystemObjectTypeAttribute>? attributes = null)
    {
        var csoType = new ConnectedSystemObjectType
        {
            ConnectedSystemId = connectedSystemId,
            Name = name,
            Selected = true,
            Attributes = attributes ?? new List<ConnectedSystemObjectTypeAttribute>
            {
                new()
                {
                    Name = "ExternalId",
                    Type = AttributeDataType.Guid,
                    IsExternalId = true,
                    Selected = true
                },
                new()
                {
                    Name = "DisplayName",
                    Type = AttributeDataType.Text,
                    Selected = true
                },
                new()
                {
                    Name = "EmployeeId",
                    Type = AttributeDataType.Text,
                    Selected = true
                }
            }
        };

        DbContext.ConnectedSystemObjectTypes.Add(csoType);
        await DbContext.SaveChangesAsync();

        SyncRepo.SeedObjectType(csoType);

        return csoType;
    }

    /// <summary>
    /// Creates a Connected System Object (CSO) representing an identity in an external system.
    /// </summary>
    protected async Task<ConnectedSystemObject> CreateCsoAsync(
        int connectedSystemId,
        ConnectedSystemObjectType csoType,
        string displayName,
        string? employeeId = null,
        DateTime? created = null,
        DateTime? lastUpdated = null)
    {
        // Get the attributes from the csoType
        var externalIdAttr = csoType.Attributes.First(a => a.IsExternalId);
        var displayNameAttr = csoType.Attributes.FirstOrDefault(a => a.Name == "DisplayName");
        var employeeIdAttr = csoType.Attributes.FirstOrDefault(a => a.Name == "EmployeeId");

        // Create the CSO
        var cso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = connectedSystemId,
            TypeId = csoType.Id,
            Type = csoType,
            ConnectedSystem = SyncRepo.ConnectedSystems.TryGetValue(connectedSystemId, out var cs) ? cs : null!,
            Created = created ?? DateTime.UtcNow,
            LastUpdated = lastUpdated
        };

        // Add attribute values
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = externalIdAttr.Id,
            Attribute = externalIdAttr,
            GuidValue = Guid.NewGuid()
        });

        if (displayNameAttr != null)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = displayNameAttr.Id,
                Attribute = displayNameAttr,
                StringValue = displayName
            });
        }

        if (employeeId != null && employeeIdAttr != null)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                Id = Guid.NewGuid(),
                AttributeId = employeeIdAttr.Id,
                Attribute = employeeIdAttr,
                StringValue = employeeId
            });
        }

        SyncRepo.SeedConnectedSystemObject(cso);

        await Task.CompletedTask;
        return cso;
    }

    #endregion

    #region Helper Methods - Metaverse

    /// <summary>
    /// Creates a Metaverse Object Type with attributes.
    /// </summary>
    protected async Task<MetaverseObjectType> CreateMvObjectTypeAsync(string name)
    {
        // Create the MV type first without attributes
        var mvType = new MetaverseObjectType
        {
            Name = name,
            PluralName = name + "s",
            BuiltIn = false,
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            Attributes = new List<MetaverseAttribute>(),
            ExampleDataTemplateAttributes = new List<JIM.Models.ExampleData.ExampleDataTemplateAttribute>(),
            PredefinedSearches = new List<JIM.Models.Search.PredefinedSearch>(),
            DeletionTriggerConnectedSystemIds = new List<int>()
        };

        DbContext.MetaverseObjectTypes.Add(mvType);
        await DbContext.SaveChangesAsync();

        // Now add attributes separately
        var displayNameAttr = new MetaverseAttribute
        {
            Name = "DisplayName",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };
        var employeeIdAttr = new MetaverseAttribute
        {
            Name = "EmployeeId",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };

        var typeAttr = new MetaverseAttribute
        {
            Name = "Type",
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };

        DbContext.MetaverseAttributes.Add(displayNameAttr);
        DbContext.MetaverseAttributes.Add(employeeIdAttr);
        DbContext.MetaverseAttributes.Add(typeAttr);
        await DbContext.SaveChangesAsync();

        // Refresh the mvType's attributes collection
        mvType.Attributes.Add(displayNameAttr);
        mvType.Attributes.Add(employeeIdAttr);
        mvType.Attributes.Add(typeAttr);

        return mvType;
    }

    #endregion

    #region Helper Methods - Sync Rules

    /// <summary>
    /// Creates an import sync rule that maps CSOs to MVOs.
    /// </summary>
    protected async Task<SyncRule> CreateImportSyncRuleAsync(
        int connectedSystemId,
        ConnectedSystemObjectType csoType,
        MetaverseObjectType mvType,
        string name,
        bool enableProjection = true)
    {
        var syncRule = new SyncRule
        {
            ConnectedSystemId = connectedSystemId,
            Name = name,
            Direction = SyncRuleDirection.Import,
            Enabled = true,
            ConnectedSystemObjectTypeId = csoType.Id,
            ConnectedSystemObjectType = csoType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType,
            ProjectToMetaverse = enableProjection
        };

        DbContext.SyncRules.Add(syncRule);
        await DbContext.SaveChangesAsync();

        SyncRepo.SeedSyncRule(syncRule);

        return syncRule;
    }

    #endregion

    #region Helper Methods - Run Profiles

    /// <summary>
    /// Creates a run profile for a Connected System.
    /// </summary>
    protected async Task<ConnectedSystemRunProfile> CreateRunProfileAsync(
        int connectedSystemId,
        string name,
        ConnectedSystemRunType runType)
    {
        var runProfile = new ConnectedSystemRunProfile
        {
            ConnectedSystemId = connectedSystemId,
            Name = name,
            RunType = runType
        };

        // Detach modified entities to avoid EF trying to persist processor-modified properties
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList())
            entry.State = EntityState.Detached;
        DbContext.ConnectedSystemRunProfiles.Add(runProfile);
        await DbContext.SaveChangesAsync();

        return runProfile;
    }

    #endregion

    #region Helper Methods - Activities

    /// <summary>
    /// Creates an Activity to track sync progress.
    /// </summary>
    protected async Task<Activity> CreateActivityAsync(
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

        // Detach modified entities to avoid EF trying to persist processor-modified properties
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList())
            entry.State = EntityState.Detached;
        DbContext.Activities.Add(activity);
        await DbContext.SaveChangesAsync();

        SyncRepo.SeedActivity(activity);

        return activity;
    }

    #endregion

    #region Helper Methods - Utilities

    /// <summary>
    /// Reloads an entity from the SyncRepo or DbContext to get the latest values.
    /// Essential after processors modify entities.
    /// </summary>
    protected Task<T> ReloadEntityAsync<T>(T entity) where T : class
    {

        // For entities stored in SyncRepo, return the current reference directly
        var idProperty = typeof(T).GetProperty("Id");
        if (idProperty != null)
        {
            var id = idProperty.GetValue(entity);

            if (id is Guid guidId)
            {
                if (typeof(T) == typeof(Activity) && SyncRepo.Activities.TryGetValue(guidId, out var activity))
                    return Task.FromResult((T)(object)activity);
                if (typeof(T) == typeof(ConnectedSystemObject) && SyncRepo.ConnectedSystemObjects.TryGetValue(guidId, out var cso))
                    return Task.FromResult((T)(object)cso);
                if (typeof(T) == typeof(MetaverseObject) && SyncRepo.MetaverseObjects.TryGetValue(guidId, out var mvo))
                    return Task.FromResult((T)(object)mvo);
            }

            if (id is int intId)
            {
                if (typeof(T) == typeof(ConnectedSystem) && SyncRepo.ConnectedSystems.TryGetValue(intId, out var cs))
                    return Task.FromResult((T)(object)cs);
            }
        }

        // Fallback to DbContext for entities not in SyncRepo
        return Task.FromResult(entity);
    }

    /// <summary>
    /// Updates a CSO's LastUpdated timestamp to simulate a modification.
    /// </summary>
    protected Task ModifyCsoAsync(ConnectedSystemObject cso, DateTime? modifiedAt = null)
    {
        cso.LastUpdated = modifiedAt ?? DateTime.UtcNow;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Creates multiple CSOs for bulk testing.
    /// </summary>
    protected async Task<List<ConnectedSystemObject>> CreateCsosAsync(
        int connectedSystemId,
        ConnectedSystemObjectType csoType,
        int count,
        string namePrefix = "User")
    {
        var csos = new List<ConnectedSystemObject>();
        var baseTime = DateTime.UtcNow.AddMinutes(-10); // Created in the past

        for (int i = 0; i < count; i++)
        {
            var cso = await CreateCsoAsync(
                connectedSystemId,
                csoType,
                $"{namePrefix} {i}",
                $"EMP{i:D6}",
                created: baseTime);
            csos.Add(cso);
        }

        return csos;
    }

    #endregion
}
