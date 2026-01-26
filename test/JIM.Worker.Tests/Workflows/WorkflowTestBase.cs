using JIM.Application;
using JIM.Data;
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
        Jim = new JimApplication(Repository);

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
            ConnectedSystemId = connectedSystemId,
            TypeId = csoType.Id,
            Created = created ?? DateTime.UtcNow,
            LastUpdated = lastUpdated
        };

        // Add attribute values
        cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = externalIdAttr.Id,
            GuidValue = Guid.NewGuid()
        });

        if (displayNameAttr != null)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = displayNameAttr.Id,
                StringValue = displayName
            });
        }

        if (employeeId != null && employeeIdAttr != null)
        {
            cso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
            {
                AttributeId = employeeIdAttr.Id,
                StringValue = employeeId
            });
        }

        DbContext.ConnectedSystemObjects.Add(cso);
        await DbContext.SaveChangesAsync();

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
            DataGenerationTemplateAttributes = new List<JIM.Models.DataGeneration.DataGenerationTemplateAttribute>(),
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

        DbContext.MetaverseAttributes.Add(displayNameAttr);
        DbContext.MetaverseAttributes.Add(employeeIdAttr);
        await DbContext.SaveChangesAsync();

        // Refresh the mvType's attributes collection
        mvType.Attributes.Add(displayNameAttr);
        mvType.Attributes.Add(employeeIdAttr);

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

        DbContext.Activities.Add(activity);
        await DbContext.SaveChangesAsync();

        return activity;
    }

    #endregion

    #region Helper Methods - Utilities

    /// <summary>
    /// Reloads an entity from the database to get the latest values.
    /// Essential after processors modify entities.
    /// </summary>
    protected async Task<T> ReloadEntityAsync<T>(T entity) where T : class
    {
        DbContext.Entry(entity).State = EntityState.Detached;

        // For entities with Id property
        var idProperty = typeof(T).GetProperty("Id");
        if (idProperty != null)
        {
            var id = idProperty.GetValue(entity);
            var reloaded = await DbContext.Set<T>().FindAsync(id);
            return reloaded ?? entity;
        }

        return entity;
    }

    /// <summary>
    /// Updates a CSO's LastUpdated timestamp to simulate a modification.
    /// </summary>
    protected async Task ModifyCsoAsync(ConnectedSystemObject cso, DateTime? modifiedAt = null)
    {
        // Reload the entity from the database to get a tracked instance
        var trackedCso = await DbContext.ConnectedSystemObjects.FindAsync(cso.Id);
        if (trackedCso != null)
        {
            trackedCso.LastUpdated = modifiedAt ?? DateTime.UtcNow;
            await DbContext.SaveChangesAsync();
            // Update the caller's reference
            cso.LastUpdated = trackedCso.LastUpdated;
        }
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
