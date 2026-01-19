using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Workflow tests for MVO deletion rules.
/// These tests verify the full lifecycle of object deletion:
/// 1. CSO becomes Obsolete (via import or sync)
/// 2. CSO is disconnected from MVO during sync
/// 3. MVO deletion rule is evaluated
/// 4. MVO is marked for deletion (LastConnectorDisconnectedDate set)
/// 5. Housekeeping deletes MVO and creates delete pending exports for downstream CSOs
///
/// Test scenarios cover:
/// - DeletionRule.Manual - no automatic deletion
/// - DeletionRule.WhenLastConnectorDisconnected - delete when all CSOs are gone
/// - DeletionTriggerConnectedSystemIds - delete when specific system disconnects (even if other CSOs remain)
/// - DeletionGracePeriodDays - immediate vs delayed deletion
/// </summary>
[TestFixture]
public class DeletionRuleWorkflowTests : WorkflowTestBase
{
    #region DeletionRule.Manual Tests

    /// <summary>
    /// Verifies that MVOs with DeletionRule=Manual are not marked for deletion
    /// even when all CSOs are disconnected.
    /// </summary>
    [Test]
    public async Task Manual_WhenLastCsoDisconnected_MvoNotMarkedForDeletionAsync()
    {
        // Arrange: Create Source system with a CSO that projects to an MVO
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync("Person", MetaverseObjectDeletionRule.Manual);
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        // Create a CSO
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith");

        // Run Full Sync to project the CSO to MVO
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(Jim, sourceSystem, fullSyncProfile, fullSyncActivity, cts1)
            .PerformFullSyncAsync();

        // Verify MVO was created and CSO is joined
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");
        var mvoId = cso.MetaverseObjectId!.Value;

        // Mark CSO as Obsolete (simulating a Delete from delta import)
        await MarkCsoAsObsoleteAsync(cso);

        // Run Delta Sync to process the Obsolete CSO
        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(Jim, sourceSystem, deltaSyncProfile, deltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO should NOT be marked for deletion (Manual rule)
        var mvo = await DbContext.MetaverseObjects.FindAsync(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Null,
            "MVO with DeletionRule=Manual should NOT have LastConnectorDisconnectedDate set, " +
            "even when all CSOs are disconnected");
    }

    #endregion

    #region DeletionRule.WhenLastConnectorDisconnected Tests

    /// <summary>
    /// Verifies that MVOs with DeletionRule=WhenLastConnectorDisconnected are marked
    /// for deletion when the last CSO is disconnected.
    /// </summary>
    [Test]
    public async Task WhenLastConnectorDisconnected_WhenLastCsoDisconnected_MvoMarkedForDeletionAsync()
    {
        // Arrange: Create Source system with a CSO that projects to an MVO
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriodDays: 0);
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        // Create a CSO
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith");

        // Run Full Sync to project the CSO to MVO
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(Jim, sourceSystem, fullSyncProfile, fullSyncActivity, cts1)
            .PerformFullSyncAsync();

        // Verify MVO was created and CSO is joined
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");
        var mvoId = cso.MetaverseObjectId!.Value;

        // Mark CSO as Obsolete (simulating a Delete from delta import)
        await MarkCsoAsObsoleteAsync(cso);

        // Run Delta Sync to process the Obsolete CSO
        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(Jim, sourceSystem, deltaSyncProfile, deltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO should be marked for deletion
        var mvo = await DbContext.MetaverseObjects.FindAsync(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist (deletion is by housekeeping)");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Not.Null,
            "MVO with DeletionRule=WhenLastConnectorDisconnected should have LastConnectorDisconnectedDate set " +
            "when the last CSO is disconnected");
        Assert.That(mvo.LastConnectorDisconnectedDate!.Value, Is.EqualTo(DateTime.UtcNow).Within(TimeSpan.FromMinutes(1)),
            "LastConnectorDisconnectedDate should be approximately now");
    }

    /// <summary>
    /// Verifies that MVOs with multiple CSOs are NOT marked for deletion when only
    /// one CSO is disconnected (other CSOs still connected).
    /// </summary>
    [Test]
    public async Task WhenLastConnectorDisconnected_WhenOneCsoDisconnectedButOthersRemain_MvoNotMarkedAsync()
    {
        // Arrange: Create two Source systems, each with a CSO that joins to the same MVO
        var sourceSystem1 = await CreateConnectedSystemAsync("Source HR System");
        var sourceSystem2 = await CreateConnectedSystemAsync("Source AD System");
        var sourceType1 = await CreateCsoTypeAsync(sourceSystem1.Id, "User");
        var sourceType2 = await CreateCsoTypeAsync(sourceSystem2.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriodDays: 0);

        // Create sync rules with matching
        var syncRule1 = await CreateImportSyncRuleAsync(sourceSystem1.Id, sourceType1, mvType, "HR Import");
        var syncRule2 = await CreateImportSyncRuleAsync(sourceSystem2.Id, sourceType2, mvType, "AD Import");

        // Create matching rules so both CSOs join to the same MVO
        await CreateMatchingRuleAsync(sourceType1, mvType, "EmployeeId");
        await CreateMatchingRuleAsync(sourceType2, mvType, "EmployeeId");

        // Create CSOs with matching employee IDs
        var cso1 = await CreateCsoAsync(sourceSystem1.Id, sourceType1, "John Smith", "EMP001");
        var cso2 = await CreateCsoAsync(sourceSystem2.Id, sourceType2, "John Smith", "EMP001");

        // Run Full Sync on both systems to create MVO and join both CSOs
        var fullSyncProfile1 = await CreateRunProfileAsync(sourceSystem1.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncProfile2 = await CreateRunProfileAsync(sourceSystem2.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);

        var activity1 = await CreateActivityAsync(sourceSystem1.Id, fullSyncProfile1, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(Jim, sourceSystem1, fullSyncProfile1, activity1, cts1)
            .PerformFullSyncAsync();

        sourceSystem2 = await ReloadEntityAsync(sourceSystem2);
        var activity2 = await CreateActivityAsync(sourceSystem2.Id, fullSyncProfile2, ConnectedSystemRunType.FullSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(Jim, sourceSystem2, fullSyncProfile2, activity2, cts2)
            .PerformFullSyncAsync();

        // Verify both CSOs are joined to the same MVO
        cso1 = await ReloadEntityAsync(cso1);
        cso2 = await ReloadEntityAsync(cso2);
        Assert.That(cso1.MetaverseObjectId, Is.Not.Null, "CSO1 should be joined to MVO");
        Assert.That(cso2.MetaverseObjectId, Is.Not.Null, "CSO2 should be joined to MVO");
        Assert.That(cso1.MetaverseObjectId, Is.EqualTo(cso2.MetaverseObjectId),
            "Both CSOs should be joined to the same MVO");
        var mvoId = cso1.MetaverseObjectId!.Value;

        // Mark only CSO1 as Obsolete (simulating a Delete from delta import on system 1)
        await MarkCsoAsObsoleteAsync(cso1);

        // Run Delta Sync on system 1 to process the Obsolete CSO
        var deltaSyncProfile1 = await CreateRunProfileAsync(sourceSystem1.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem1 = await ReloadEntityAsync(sourceSystem1);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem1.Id, deltaSyncProfile1, ConnectedSystemRunType.DeltaSynchronisation);
        var cts3 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(Jim, sourceSystem1, deltaSyncProfile1, deltaSyncActivity, cts3)
            .PerformDeltaSyncAsync();

        // Assert: MVO should NOT be marked for deletion (CSO2 still connected)
        var mvo = await DbContext.MetaverseObjects.FindAsync(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Null,
            "MVO should NOT have LastConnectorDisconnectedDate set when other CSOs are still connected");
    }

    #endregion

    #region DeletionTriggerConnectedSystemIds Tests

    /// <summary>
    /// Verifies that MVOs are marked for deletion when a specific trigger system
    /// disconnects, even if other CSOs from non-trigger systems remain connected.
    /// This is the key feature for "delete from Target when deleted from Source".
    /// </summary>
    [Test]
    public async Task DeletionTrigger_WhenTriggerSystemDisconnects_MvoMarkedForDeletionAsync()
    {
        // Arrange: Create Source (HR) and Target (AD) systems
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var targetSystem = await CreateConnectedSystemAsync("Target AD System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "User");

        // Create MV type with deletion trigger on Source system only
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriodDays: 0,
            triggerConnectedSystemIds: new List<int> { sourceSystem.Id });

        // Create sync rules
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var targetExportRule = await CreateExportSyncRuleAsync(targetSystem.Id, targetType, mvType, "AD Export");

        // Create matching rules
        await CreateMatchingRuleAsync(sourceType, mvType, "EmployeeId");
        await CreateMatchingRuleAsync(targetType, mvType, "EmployeeId");

        // Create Source CSO
        var sourceCso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        // Run Full Sync on Source to create MVO
        var sourceFullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var sourceFullSyncActivity = await CreateActivityAsync(sourceSystem.Id, sourceFullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(Jim, sourceSystem, sourceFullSyncProfile, sourceFullSyncActivity, cts1)
            .PerformFullSyncAsync();

        // Verify MVO created
        sourceCso = await ReloadEntityAsync(sourceCso);
        Assert.That(sourceCso.MetaverseObjectId, Is.Not.Null, "Source CSO should be joined to MVO");
        var mvoId = sourceCso.MetaverseObjectId!.Value;

        // Create Target CSO and join it to the MVO (simulating a provisioned export)
        var targetCso = await CreateCsoAsync(targetSystem.Id, targetType, "John Smith", "EMP001");
        targetCso.MetaverseObjectId = mvoId;
        targetCso.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        targetCso.DateJoined = DateTime.UtcNow;
        await DbContext.SaveChangesAsync();

        // Verify both CSOs are joined
        targetCso = await ReloadEntityAsync(targetCso);
        Assert.That(targetCso.MetaverseObjectId, Is.EqualTo(mvoId), "Target CSO should be joined to same MVO");

        // Mark Source CSO as Obsolete (simulating a Delete from delta import)
        await MarkCsoAsObsoleteAsync(sourceCso);

        // Run Delta Sync on Source to process the Obsolete CSO
        var sourceDeltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var sourceDeltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, sourceDeltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(Jim, sourceSystem, sourceDeltaSyncProfile, sourceDeltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO SHOULD be marked for deletion because Source (trigger system) disconnected
        // even though Target CSO is still connected
        var mvo = await DbContext.MetaverseObjects.FindAsync(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist (deletion is by housekeeping)");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Not.Null,
            "MVO SHOULD have LastConnectorDisconnectedDate set when a DeletionTriggerConnectedSystemId disconnects, " +
            "even if other CSOs are still connected. This is the key feature for 'delete from Target when deleted from Source'.");
    }

    /// <summary>
    /// Verifies that MVOs are NOT marked for deletion when a non-trigger system
    /// disconnects, even if all its CSOs are gone (because trigger system CSOs remain).
    /// </summary>
    [Test]
    public async Task DeletionTrigger_WhenNonTriggerSystemDisconnects_MvoNotMarkedAsync()
    {
        // Arrange: Create Source (HR) and Target (AD) systems
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var targetSystem = await CreateConnectedSystemAsync("Target AD System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "User");

        // Create MV type with deletion trigger on Source system only
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriodDays: 0,
            triggerConnectedSystemIds: new List<int> { sourceSystem.Id });

        // Create sync rules
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        await CreateImportSyncRuleAsync(targetSystem.Id, targetType, mvType, "AD Import");

        // Create matching rules
        await CreateMatchingRuleAsync(sourceType, mvType, "EmployeeId");
        await CreateMatchingRuleAsync(targetType, mvType, "EmployeeId");

        // Create CSOs with matching employee IDs
        var sourceCso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");
        var targetCso = await CreateCsoAsync(targetSystem.Id, targetType, "John Smith", "EMP001");

        // Run Full Sync on both systems
        var sourceFullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var targetFullSyncProfile = await CreateRunProfileAsync(targetSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);

        var activity1 = await CreateActivityAsync(sourceSystem.Id, sourceFullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(Jim, sourceSystem, sourceFullSyncProfile, activity1, cts1)
            .PerformFullSyncAsync();

        targetSystem = await ReloadEntityAsync(targetSystem);
        var activity2 = await CreateActivityAsync(targetSystem.Id, targetFullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(Jim, targetSystem, targetFullSyncProfile, activity2, cts2)
            .PerformFullSyncAsync();

        // Verify both CSOs are joined to the same MVO
        sourceCso = await ReloadEntityAsync(sourceCso);
        targetCso = await ReloadEntityAsync(targetCso);
        Assert.That(sourceCso.MetaverseObjectId, Is.EqualTo(targetCso.MetaverseObjectId),
            "Both CSOs should be joined to the same MVO");
        var mvoId = sourceCso.MetaverseObjectId!.Value;

        // Mark Target CSO as Obsolete (non-trigger system)
        await MarkCsoAsObsoleteAsync(targetCso);

        // Run Delta Sync on Target to process the Obsolete CSO
        var targetDeltaSyncProfile = await CreateRunProfileAsync(targetSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        targetSystem = await ReloadEntityAsync(targetSystem);
        var deltaSyncActivity = await CreateActivityAsync(targetSystem.Id, targetDeltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts3 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(Jim, targetSystem, targetDeltaSyncProfile, deltaSyncActivity, cts3)
            .PerformDeltaSyncAsync();

        // Assert: MVO should NOT be marked for deletion (non-trigger system disconnected)
        var mvo = await DbContext.MetaverseObjects.FindAsync(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Null,
            "MVO should NOT have LastConnectorDisconnectedDate set when a non-trigger system disconnects " +
            "(Source CSO is still connected and Source is the only trigger system)");
    }

    #endregion

    #region Grace Period Tests

    /// <summary>
    /// Verifies that MVOs with a grace period are marked for deletion but not immediately eligible.
    /// </summary>
    [Test]
    public async Task GracePeriod_WhenSet_MvoMarkedButNotImmediatelyEligibleAsync()
    {
        // Arrange: Create system with a 30-day grace period
        var sourceSystem = await CreateConnectedSystemAsync("Source HR System");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriodDays: 30);
        await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        // Create a CSO
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith");

        // Run Full Sync
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        var cts1 = new CancellationTokenSource();
        await new SyncFullSyncTaskProcessor(Jim, sourceSystem, fullSyncProfile, fullSyncActivity, cts1)
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;

        // Mark CSO as Obsolete
        await MarkCsoAsObsoleteAsync(cso);

        // Run Delta Sync
        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        var cts2 = new CancellationTokenSource();
        await new SyncDeltaSyncTaskProcessor(Jim, sourceSystem, deltaSyncProfile, deltaSyncActivity, cts2)
            .PerformDeltaSyncAsync();

        // Assert: MVO should be marked for deletion but not immediately eligible
        var mvo = await DbContext.MetaverseObjects.FindAsync(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist");
        Assert.That(mvo!.LastConnectorDisconnectedDate, Is.Not.Null, "MVO should be marked for deletion");

        // Check the computed properties
        Assert.That(mvo.IsPendingDeletion, Is.True, "MVO should report IsPendingDeletion=true");
        Assert.That(mvo.DeletionEligibleDate, Is.Not.Null, "MVO should have a DeletionEligibleDate");
        Assert.That(mvo.DeletionEligibleDate!.Value,
            Is.EqualTo(mvo.LastConnectorDisconnectedDate!.Value.AddDays(30)).Within(TimeSpan.FromSeconds(1)),
            "DeletionEligibleDate should be 30 days after LastConnectorDisconnectedDate");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a Metaverse Object Type with specific deletion rule settings.
    /// </summary>
    protected async Task<MetaverseObjectType> CreateMvObjectTypeWithDeletionRuleAsync(
        string name,
        MetaverseObjectDeletionRule deletionRule,
        int? gracePeriodDays = null,
        List<int>? triggerConnectedSystemIds = null)
    {
        var mvType = new MetaverseObjectType
        {
            Name = name,
            PluralName = name + "s",
            BuiltIn = false,
            DeletionRule = deletionRule,
            DeletionGracePeriodDays = gracePeriodDays,
            DeletionTriggerConnectedSystemIds = triggerConnectedSystemIds ?? new List<int>(),
            Attributes = new List<MetaverseAttribute>(),
            DataGenerationTemplateAttributes = new List<JIM.Models.DataGeneration.DataGenerationTemplateAttribute>(),
            PredefinedSearches = new List<JIM.Models.Search.PredefinedSearch>()
        };

        DbContext.MetaverseObjectTypes.Add(mvType);
        await DbContext.SaveChangesAsync();

        // Add attributes
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

        mvType.Attributes.Add(displayNameAttr);
        mvType.Attributes.Add(employeeIdAttr);

        return mvType;
    }

    /// <summary>
    /// Creates an export sync rule.
    /// </summary>
    protected async Task<SyncRule> CreateExportSyncRuleAsync(
        int connectedSystemId,
        ConnectedSystemObjectType csoType,
        MetaverseObjectType mvType,
        string name,
        bool enableProvisioning = true)
    {
        var syncRule = new SyncRule
        {
            ConnectedSystemId = connectedSystemId,
            Name = name,
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            ConnectedSystemObjectTypeId = csoType.Id,
            ConnectedSystemObjectType = csoType,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType,
            ProvisionToConnectedSystem = enableProvisioning
        };

        DbContext.SyncRules.Add(syncRule);
        await DbContext.SaveChangesAsync();

        return syncRule;
    }

    /// <summary>
    /// Creates a matching rule for joining CSOs to MVOs.
    /// Matching rules belong to the ConnectedSystemObjectType and define how to find/match existing MVOs.
    /// </summary>
    protected async Task<ObjectMatchingRule> CreateMatchingRuleAsync(
        ConnectedSystemObjectType csoType,
        MetaverseObjectType mvType,
        string attributeName)
    {
        var csoAttr = csoType.Attributes.First(a => a.Name == attributeName);
        var mvAttr = mvType.Attributes.First(a => a.Name == attributeName);

        var matchingRule = new ObjectMatchingRule
        {
            Order = 1,
            ConnectedSystemObjectType = csoType,
            ConnectedSystemObjectTypeId = csoType.Id,
            TargetMetaverseAttribute = mvAttr,
            TargetMetaverseAttributeId = mvAttr.Id,
            Sources = new List<ObjectMatchingRuleSource>
            {
                new()
                {
                    Order = 1,
                    ConnectedSystemAttribute = csoAttr,
                    ConnectedSystemAttributeId = csoAttr.Id
                }
            }
        };

        DbContext.ObjectMatchingRules.Add(matchingRule);
        await DbContext.SaveChangesAsync();

        return matchingRule;
    }

    /// <summary>
    /// Marks a CSO as Obsolete (simulating a Delete from delta import).
    /// </summary>
    protected async Task MarkCsoAsObsoleteAsync(ConnectedSystemObject cso)
    {
        var trackedCso = await DbContext.ConnectedSystemObjects.FindAsync(cso.Id);
        if (trackedCso != null)
        {
            trackedCso.Status = ConnectedSystemObjectStatus.Obsolete;
            trackedCso.LastUpdated = DateTime.UtcNow;
            await DbContext.SaveChangesAsync();
            cso.Status = trackedCso.Status;
            cso.LastUpdated = trackedCso.LastUpdated;
        }
    }

    #endregion
}
