using JIM.Application;
using JIM.Application.Interfaces;
using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Tests that MVO and CSO change tracking records are correctly persisted during sync.
/// Covers three scenarios:
/// <list type="bullet">
///   <item>MVO change records survive the page flush pipeline (Issue 1 regression)</item>
///   <item>CachedDisplayName is set during admin MVO creation and update (Issue 2)</item>
///   <item>Provisioned CSOs receive "Added" change records (Issue 3)</item>
/// </list>
/// </summary>
[TestFixture]
public class ChangeTrackingPersistenceWorkflowTests : WorkflowTestBase
{
    #region Issue 1: MVO change records persisted through flush pipeline

    [Test]
    public async Task FullSync_MvoChangeRecords_PersistedAfterPageFlushAsync()
    {
        // Arrange: HR system with one user
        var hrSystem = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "Person");
        var mvType = await CreateMvObjectTypeAsync("Person");

        var hrDisplayNameAttr = hrType.Attributes.First(a => a.Name == "DisplayName");
        var hrEmployeeIdAttr = hrType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");

        // Import rule: project + flow DisplayName and EmployeeId
        var importRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = hrDisplayNameAttr,
                ConnectedSystemAttributeId = hrDisplayNameAttr.Id
            }}
        });
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvEmployeeIdAttr,
            TargetMetaverseAttributeId = mvEmployeeIdAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = hrEmployeeIdAttr,
                ConnectedSystemAttributeId = hrEmployeeIdAttr.Id
            }}
        });

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "Alice Smith", "EMP001");

        // Act: Full Sync projects MVO with change tracking
        var profile = await CreateRunProfileAsync(hrSystem.Id, "HR Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(hrSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            hrSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Assert: MVO was created
        hrCso = await ReloadEntityAsync(hrCso);
        Assert.That(hrCso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to an MVO");

        var mvo = SyncRepo.MetaverseObjects[hrCso.MetaverseObjectId!.Value];

        // Assert: MVO change records were persisted (not lost by ClearChangeTracker)
        var changes = mvo.Changes.OrderBy(c => c.ChangeTime).ToList();
        Assert.That(changes, Has.Count.GreaterThanOrEqualTo(1),
            "MVO should have at least one change record after Full Sync projection");

        var projectedChange = changes.First(c =>
            c.ChangeType == ObjectChangeType.Projected || c.ChangeType == ObjectChangeType.Joined);
        Assert.That(projectedChange.AttributeChanges, Has.Count.GreaterThanOrEqualTo(2),
            "Projected change should record DisplayName and EmployeeId attribute flows");
    }

    [Test]
    public async Task DeltaSync_MvoAttributeFlowChanges_PersistedAfterPageFlushAsync()
    {
        // Arrange: Run a full sync first to establish the MVO
        var hrSystem = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "Person");
        var mvType = await CreateMvObjectTypeAsync("Person");

        var hrDisplayNameAttr = hrType.Attributes.First(a => a.Name == "DisplayName");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");

        var importRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = hrDisplayNameAttr,
                ConnectedSystemAttributeId = hrDisplayNameAttr.Id
            }}
        });

        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "Alice Smith");

        // Full Sync to project MVO
        var fullProfile = await CreateRunProfileAsync(hrSystem.Id, "HR Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullActivity = await CreateActivityAsync(hrSystem.Id, fullProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            hrSystem, fullProfile, fullActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        hrCso = await ReloadEntityAsync(hrCso);
        var mvoId = hrCso.MetaverseObjectId!.Value;
        var changeCountAfterFullSync = SyncRepo.MetaverseObjects[mvoId].Changes.Count;

        // Modify the CSO's display name to trigger a delta sync change
        var displayNameValue = hrCso.AttributeValues.First(av => av.AttributeId == hrDisplayNameAttr.Id);
        displayNameValue.StringValue = "Alice Jones";
        await ModifyCsoAsync(hrCso);

        // Act: Delta Sync detects the change and flows it to MVO
        var deltaProfile = await CreateRunProfileAsync(hrSystem.Id, "HR Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        var deltaActivity = await CreateActivityAsync(hrSystem.Id, deltaProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            hrSystem, deltaProfile, deltaActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // Assert: new MVO change records were persisted
        var mvo = SyncRepo.MetaverseObjects[mvoId];
        Assert.That(mvo.Changes.Count, Is.GreaterThan(changeCountAfterFullSync),
            "Delta Sync should produce additional MVO change records for the attribute update");

        var latestChange = mvo.Changes.OrderByDescending(c => c.ChangeTime).First();
        var displayNameAttrChange = latestChange.AttributeChanges
            .FirstOrDefault(ac => ac.Attribute?.Name == "DisplayName");
        Assert.That(displayNameAttrChange, Is.Not.Null,
            "Change record should include the DisplayName attribute flow");
    }

    #endregion

    #region Issue 2: CachedDisplayName set on admin-created MVOs

    /// <summary>
    /// Creates an MV type with a "Display Name" attribute matching the built-in constant
    /// (Constants.BuiltInAttributes.DisplayName = "Display Name").
    /// The base helper uses "DisplayName" (no space) which is the sync attribute name.
    /// </summary>
    private async Task<(MetaverseObjectType Type, MetaverseAttribute DisplayNameAttr, MetaverseAttribute EmployeeIdAttr)>
        CreateMvTypeWithBuiltInDisplayNameAsync()
    {
        var mvType = new MetaverseObjectType
        {
            Name = "Person",
            PluralName = "People",
            BuiltIn = false,
            DeletionRule = MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            Attributes = new List<MetaverseAttribute>(),
            ExampleDataTemplateAttributes = new List<JIM.Models.ExampleData.ExampleDataTemplateAttribute>(),
            PredefinedSearches = new List<JIM.Models.Search.PredefinedSearch>(),
            DeletionTriggerConnectedSystemIds = new List<int>()
        };
        DbContext.MetaverseObjectTypes.Add(mvType);
        await DbContext.SaveChangesAsync();

        // Use the built-in attribute name "Display Name" (with space)
        var displayNameAttr = new MetaverseAttribute
        {
            Name = JIM.Models.Core.Constants.BuiltInAttributes.DisplayName,
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

        mvType.Attributes.Add(displayNameAttr);
        mvType.Attributes.Add(employeeIdAttr);

        return (mvType, displayNameAttr, employeeIdAttr);
    }

    [Test]
    public async Task CreateMetaverseObject_WithDisplayName_SetsCachedDisplayNameAsync()
    {
        // Arrange
        var (mvType, displayNameAttr, _) = await CreateMvTypeWithBuiltInDisplayNameAsync();

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvType,
            Created = DateTime.UtcNow
        };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = displayNameAttr,
            AttributeId = displayNameAttr.Id,
            StringValue = "Admin User"
        });

        // Act
        await Jim.Metaverse.CreateMetaverseObjectAsync(mvo);

        // Assert
        Assert.That(mvo.CachedDisplayName, Is.EqualTo("Admin User"),
            "CachedDisplayName should be set from DisplayName when creating an MVO");
    }

    [Test]
    public async Task CreateMetaverseObject_WithoutDisplayName_CachedDisplayNameIsNullAsync()
    {
        // Arrange
        var (mvType, _, employeeIdAttr) = await CreateMvTypeWithBuiltInDisplayNameAsync();

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvType,
            Created = DateTime.UtcNow
        };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP999"
        });

        // Act
        await Jim.Metaverse.CreateMetaverseObjectAsync(mvo);

        // Assert
        Assert.That(mvo.CachedDisplayName, Is.Null,
            "CachedDisplayName should be null when no DisplayName attribute is present");
    }

    [Test]
    public async Task UpdateMetaverseObject_ChangingDisplayName_UpdatesCachedDisplayNameAsync()
    {
        // Arrange: create an MVO with a display name
        var (mvType, displayNameAttr, _) = await CreateMvTypeWithBuiltInDisplayNameAsync();

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvType,
            Created = DateTime.UtcNow
        };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = displayNameAttr,
            AttributeId = displayNameAttr.Id,
            StringValue = "Old Name"
        });
        await Jim.Metaverse.CreateMetaverseObjectAsync(mvo);
        Assert.That(mvo.CachedDisplayName, Is.EqualTo("Old Name"));

        // Act: update the display name
        var updatedDisplayName = new MetaverseObjectAttributeValue
        {
            Attribute = displayNameAttr,
            AttributeId = displayNameAttr.Id,
            StringValue = "New Name"
        };
        mvo.AttributeValues.Clear();
        mvo.AttributeValues.Add(updatedDisplayName);

        await Jim.Metaverse.UpdateMetaverseObjectAsync(
            mvo,
            additions: new List<MetaverseObjectAttributeValue> { updatedDisplayName },
            removals: new List<MetaverseObjectAttributeValue>());

        // Assert
        Assert.That(mvo.CachedDisplayName, Is.EqualTo("New Name"),
            "CachedDisplayName should be updated when DisplayName attribute changes");
    }

    [Test]
    public async Task CreateMetaverseObject_ChangeRecords_PersistedViaSingleObjectCreateAsync()
    {
        // Arrange: MVO with attributes created via the single-object path
        // (CreateMetaverseObjectAsync), which uses explicit EntityState management
        // rather than AddRange graph traversal.
        var (mvType, displayNameAttr, employeeIdAttr) = await CreateMvTypeWithBuiltInDisplayNameAsync();

        var mvo = new MetaverseObject
        {
            Id = Guid.NewGuid(),
            Type = mvType,
            Created = DateTime.UtcNow
        };
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = displayNameAttr,
            AttributeId = displayNameAttr.Id,
            StringValue = "Admin User"
        });
        mvo.AttributeValues.Add(new MetaverseObjectAttributeValue
        {
            Attribute = employeeIdAttr,
            AttributeId = employeeIdAttr.Id,
            StringValue = "EMP001"
        });

        // Act
        await Jim.Metaverse.CreateMetaverseObjectAsync(
            mvo,
            changeInitiatorType: MetaverseObjectChangeInitiatorType.System);

        // Assert: change records were persisted (not silently dropped)
        var reloaded = await DbContext.MetaverseObjects
            .Include(m => m.Changes)
            .ThenInclude(c => c.AttributeChanges)
            .ThenInclude(ac => ac.ValueChanges)
            .SingleAsync(m => m.Id == mvo.Id);

        Assert.That(reloaded.Changes, Has.Count.EqualTo(1),
            "MVO should have one 'Created' change record persisted in the database");

        var change = reloaded.Changes[0];
        Assert.That(change.ChangeType, Is.EqualTo(ObjectChangeType.Created));
        Assert.That(change.ChangeInitiatorType, Is.EqualTo(MetaverseObjectChangeInitiatorType.System));
        Assert.That(change.AttributeChanges, Has.Count.EqualTo(2),
            "Change record should capture both attribute additions");

        // Verify attribute values were recorded
        var displayNameChange = change.AttributeChanges.Single(ac => ac.AttributeName == displayNameAttr.Name);
        Assert.That(displayNameChange.ValueChanges, Has.Count.EqualTo(1));
        Assert.That(displayNameChange.ValueChanges[0].StringValue, Is.EqualTo("Admin User"));
        Assert.That(displayNameChange.ValueChanges[0].ValueChangeType, Is.EqualTo(ValueChangeType.Add));
    }

    #endregion

    #region Issue 3: Provisioned CSOs receive "Added" change records

    [Test]
    public async Task FullSync_ProvisionedCso_ReceivesAddedChangeRecordAsync()
    {
        // Arrange: HR source + AD target with provisioning export rule
        var hrSystem = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "Person");
        var mvType = await CreateMvObjectTypeAsync("Person");

        var hrDisplayNameAttr = hrType.Attributes.First(a => a.Name == "DisplayName");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");

        // Import rule
        var importRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = hrDisplayNameAttr,
                ConnectedSystemAttributeId = hrDisplayNameAttr.Id
            }}
        });

        // Target system with provisioning export rule
        var targetSystem = await CreateConnectedSystemAsync("AD");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "User");
        var targetDisplayNameAttr = targetType.Attributes.First(a => a.Name == "DisplayName");

        var exportRule = new SyncRule
        {
            ConnectedSystemId = targetSystem.Id,
            Name = "AD Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = targetType.Id,
            ConnectedSystemObjectType = targetType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType,
            ProvisionToConnectedSystem = true
        };

        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                MetaverseAttribute = mvDisplayNameAttr,
                MetaverseAttributeId = mvDisplayNameAttr.Id
            }}
        });

        DbContext.SyncRules.Add(exportRule);
        await DbContext.SaveChangesAsync();
        SyncRepo.SeedSyncRule(exportRule);

        // Create HR CSO
        var hrCso = await CreateCsoAsync(hrSystem.Id, hrType, "Alice Smith");

        // Act: Full Sync triggers projection + export evaluation (provisioning)
        var profile = await CreateRunProfileAsync(hrSystem.Id, "HR Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(hrSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            hrSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Assert: provisioned CSO was created
        var provisionedCso = SyncRepo.ConnectedSystemObjects.Values
            .FirstOrDefault(c => c.ConnectedSystemId == targetSystem.Id);
        Assert.That(provisionedCso, Is.Not.Null,
            "Export provisioning should create a CSO in the target system");

        // Assert: RPEI has an "Added" CSO change record
        var rpeis = activity.RunProfileExecutionItems;
        var rpeiWithCsoChange = rpeis.FirstOrDefault(r =>
            r.ConnectedSystemObjectChange != null &&
            r.ConnectedSystemObjectChange.ConnectedSystemId == targetSystem.Id);
        Assert.That(rpeiWithCsoChange, Is.Not.Null,
            "An RPEI should have an 'Added' CSO change record for the provisioned CSO");
        Assert.That(rpeiWithCsoChange!.ConnectedSystemObjectChange!.ChangeType, Is.EqualTo(ObjectChangeType.Added),
            "CSO change type should be 'Added' for a newly provisioned CSO");
        Assert.That(rpeiWithCsoChange.ConnectedSystemObjectChange.ConnectedSystemObjectId, Is.EqualTo(provisionedCso!.Id),
            "CSO change should reference the provisioned CSO");
    }

    [Test]
    public async Task FullSync_MultipleProvisionedCsos_EachReceivesChangeRecordAsync()
    {
        // Arrange: HR source with two users + AD target with provisioning
        var hrSystem = await CreateConnectedSystemAsync("HR");
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "Person");
        var mvType = await CreateMvObjectTypeAsync("Person");

        var hrDisplayNameAttr = hrType.Attributes.First(a => a.Name == "DisplayName");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");

        var importRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = hrDisplayNameAttr,
                ConnectedSystemAttributeId = hrDisplayNameAttr.Id
            }}
        });

        var targetSystem = await CreateConnectedSystemAsync("AD");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "User");
        var targetDisplayNameAttr = targetType.Attributes.First(a => a.Name == "DisplayName");

        var exportRule = new SyncRule
        {
            ConnectedSystemId = targetSystem.Id,
            Name = "AD Export",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = targetType.Id,
            ConnectedSystemObjectType = targetType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType,
            ProvisionToConnectedSystem = true
        };

        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                MetaverseAttribute = mvDisplayNameAttr,
                MetaverseAttributeId = mvDisplayNameAttr.Id
            }}
        });

        DbContext.SyncRules.Add(exportRule);
        await DbContext.SaveChangesAsync();
        SyncRepo.SeedSyncRule(exportRule);

        // Two HR users
        await CreateCsoAsync(hrSystem.Id, hrType, "Alice Smith");
        await CreateCsoAsync(hrSystem.Id, hrType, "Bob Jones");

        // Act
        var profile = await CreateRunProfileAsync(hrSystem.Id, "HR Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(hrSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(
            new SyncEngine(), new SyncServer(Jim), SyncRepo,
            hrSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Assert: two provisioned CSOs were created
        var provisionedCsos = SyncRepo.ConnectedSystemObjects.Values
            .Where(c => c.ConnectedSystemId == targetSystem.Id)
            .ToList();
        Assert.That(provisionedCsos, Has.Count.EqualTo(2),
            "Both HR users should produce provisioned CSOs in the target system");

        // Assert: each provisioned CSO has a corresponding "Added" change record
        var csoChangeRecords = activity.RunProfileExecutionItems
            .Where(r => r.ConnectedSystemObjectChange != null &&
                        r.ConnectedSystemObjectChange.ConnectedSystemId == targetSystem.Id &&
                        r.ConnectedSystemObjectChange.ChangeType == ObjectChangeType.Added)
            .Select(r => r.ConnectedSystemObjectChange!)
            .ToList();
        Assert.That(csoChangeRecords, Has.Count.EqualTo(2),
            "Each provisioned CSO should have an 'Added' change record");

        // Verify each change record references a distinct provisioned CSO
        var referencedCsoIds = csoChangeRecords.Select(c => c.ConnectedSystemObjectId).Distinct().ToList();
        Assert.That(referencedCsoIds, Has.Count.EqualTo(2),
            "Change records should reference distinct provisioned CSOs");
    }

    #endregion
}
