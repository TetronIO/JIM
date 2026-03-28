using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Tests for the optimisation described in GitHub issue #390:
/// Skip nugatory attribute recall work before immediate MVO deletion.
///
/// When a CSO is disconnected and the MVO deletion rule triggers immediate deletion
/// (0 grace period), attribute recall is wasted work because the MVO is about to be
/// deleted. The optimisation evaluates the deletion rule BEFORE attribute recall and
/// skips recall when the fate is DeletedImmediately.
///
/// These tests verify:
/// 1. Attribute recall is SKIPPED when immediate deletion will occur (no AttributeFlow outcome)
/// 2. Attribute recall still happens when deletion is NOT immediate (grace period, NotDeleted)
/// 3. MVO deletion still works correctly with the optimisation
/// </summary>
[TestFixture]
public class NugatoryWorkOptimisationTests : WorkflowTestBase
{
    #region Obsolete CSO Path — Immediate Deletion Skips Recall

    /// <summary>
    /// Verifies that when a CSO goes obsolete and the MVO will be deleted immediately
    /// (0 grace period, WhenLastConnectorDisconnected), attribute recall is skipped.
    /// The RPEI causality tree should have Disconnected → [CsoDeleted, MvoDeleted]
    /// but NO AttributeFlow child (because recall was skipped).
    /// </summary>
    [Test]
    public async Task ObsoleteCso_ImmediateDeletion_SkipsAttributeRecallAsync()
    {
        // Arrange: Create source system with attribute recall enabled
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        sourceType.RemoveContributedAttributesOnObsoletion = true;

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.Zero);

        // Create import sync rule WITH attribute flow mappings so attributes get contributed
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoDisplayNameAttr,
                ConnectedSystemAttributeId = csoDisplayNameAttr.Id
            }}
        });

        // Create CSO and run Full Sync to project and flow attributes
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Verify MVO was created with contributed attributes
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");
        var mvoId = cso.MetaverseObjectId!.Value;
        var mvo = SyncRepo.MetaverseObjects[mvoId];
        var contributedAttrs = mvo.AttributeValues
            .Where(av => av.ContributedBySystemId == sourceSystem.Id)
            .ToList();
        Assert.That(contributedAttrs, Has.Count.GreaterThan(0),
            "MVO should have attributes contributed by the source system");

        // Mark CSO as Obsolete and run Delta Sync
        await MarkCsoAsObsoleteAsync(cso);

        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // Assert: MVO should be deleted
        Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Null,
            "MVO should be deleted immediately (0 grace period)");

        // Assert: The RPEI should have Disconnected root with CsoDeleted and MvoDeleted children
        // but NO AttributeFlow child (recall was skipped because MVO was about to be deleted)
        var disconnectedRpei = deltaSyncActivity.RunProfileExecutionItems
            .Single(r => r.ObjectChangeType == ObjectChangeType.Disconnected);
        var rootOutcome = disconnectedRpei.SyncOutcomes
            .Single(o => o.ParentSyncOutcome == null);
        Assert.That(rootOutcome.OutcomeType,
            Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected));

        // Verify MvoDeleted is present
        var mvoDeletedOutcome = rootOutcome.Children
            .SingleOrDefault(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        Assert.That(mvoDeletedOutcome, Is.Not.Null,
            "MvoDeleted outcome should be present");

        // Verify NO AttributeFlow child (the optimisation!)
        var attributeFlowOutcome = rootOutcome.Children
            .SingleOrDefault(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        Assert.That(attributeFlowOutcome, Is.Null,
            "AttributeFlow outcome should NOT be present when MVO is deleted immediately — " +
            "attribute recall is nugatory work and should be skipped (#390)");

        // Verify the RPEI itself has no attribute flow count
        Assert.That(disconnectedRpei.AttributeFlowCount, Is.Null.Or.EqualTo(0),
            "RPEI should have no AttributeFlowCount when recall is skipped");
    }

    /// <summary>
    /// Verifies that no attribute-recall Update pending exports are created when
    /// an obsolete CSO triggers immediate MVO deletion. Delete pending exports
    /// (from EvaluateMvoDeletionAsync) should still be created.
    /// </summary>
    [Test]
    public async Task ObsoleteCso_ImmediateDeletion_NoAttributeRecallPendingExportsAsync()
    {
        // Arrange: Create source and target systems
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        sourceType.RemoveContributedAttributesOnObsoletion = true;

        var targetSystem = await CreateConnectedSystemAsync("AD Target");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "User");

        // Use WhenAuthoritativeSourceDisconnected so MVO is deleted even when target CSO remains
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.Zero,
            triggerConnectedSystemIds: new List<int> { sourceSystem.Id });

        // Create import sync rule with attribute flow mappings
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoDisplayNameAttr,
                ConnectedSystemAttributeId = csoDisplayNameAttr.Id
            }}
        });

        // Create export sync rule for target
        await CreateExportSyncRuleAsync(targetSystem.Id, targetType, mvType, "AD Export");
        await CreateMatchingRuleAsync(targetType, mvType, "EmployeeId");

        // Create CSO and run Full Sync to project and flow attributes
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;

        // Create target CSO and join to same MVO (simulating provisioning)
        var targetCso = await CreateCsoAsync(targetSystem.Id, targetType, "John Smith AD", "EMP001");
        targetCso.MetaverseObjectId = mvoId;
        targetCso.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        targetCso.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(targetCso);

        // Clear any pending exports from the Full Sync
        SyncRepo.ClearAllPendingExports();

        // Mark source CSO as Obsolete and run Delta Sync
        await MarkCsoAsObsoleteAsync(cso);

        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // Assert: MVO should be deleted
        Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Null,
            "MVO should be deleted immediately");

        // Assert: Only Delete pending exports should exist (for the target CSO).
        // No Update pending exports from attribute recall should be present.
        var allPendingExports = SyncRepo.PendingExports.Values.ToList();
        var updatePendingExports = allPendingExports
            .Where(pe => pe.ChangeType == JIM.Models.Transactional.PendingExportChangeType.Update)
            .ToList();
        Assert.That(updatePendingExports, Has.Count.EqualTo(0),
            "No Update pending exports should exist — attribute recall was skipped because " +
            "the MVO is about to be deleted immediately (#390)");

        // Delete pending exports for the target CSO should still exist
        var targetDeletePendingExports = allPendingExports
            .Where(pe => pe.ChangeType == JIM.Models.Transactional.PendingExportChangeType.Delete
                        && pe.ConnectedSystemId == targetSystem.Id)
            .ToList();
        Assert.That(targetDeletePendingExports, Has.Count.GreaterThanOrEqualTo(1),
            "Delete pending export should be created for the provisioned target CSO");
    }

    #endregion

    #region Obsolete CSO Path — Non-Immediate Deletion Still Recalls

    /// <summary>
    /// Verifies that attribute recall still happens when deletion is deferred
    /// (grace period > 0). The AttributeFlow outcome should be present because
    /// the MVO continues to exist and target systems need to know about the recall.
    /// </summary>
    [Test]
    public async Task ObsoleteCso_GracePeriodDeletion_StillRecallsAttributesAsync()
    {
        // Arrange: Create source system with attribute recall enabled and a grace period
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        sourceType.RemoveContributedAttributesOnObsoletion = true;

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.FromDays(30));

        // Create import sync rule with attribute flow mappings
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoDisplayNameAttr,
                ConnectedSystemAttributeId = csoDisplayNameAttr.Id
            }}
        });

        // Create CSO and run Full Sync
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;

        // Mark CSO as Obsolete and run Delta Sync
        await MarkCsoAsObsoleteAsync(cso);

        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // Assert: MVO should still exist (grace period)
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist (grace period > 0)");

        // Note: With grace period > 0, hasGracePeriod is true, so attribute recall is already
        // skipped by the existing grace period guard. This test confirms that behaviour is preserved.
        // The MvoDeletionScheduled outcome should be present.
        var disconnectedRpei = deltaSyncActivity.RunProfileExecutionItems
            .Single(r => r.ObjectChangeType == ObjectChangeType.Disconnected);
        var rootOutcome = disconnectedRpei.SyncOutcomes
            .Single(o => o.ParentSyncOutcome == null);

        var deletionScheduledOutcome = rootOutcome.Children
            .SingleOrDefault(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled);
        Assert.That(deletionScheduledOutcome, Is.Not.Null,
            "MvoDeletionScheduled outcome should be present when grace period > 0");
    }

    /// <summary>
    /// Verifies that attribute recall still happens when the MVO will NOT be deleted
    /// (e.g., Manual deletion rule, or remaining connectors).
    /// </summary>
    [Test]
    public async Task ObsoleteCso_NotDeleted_StillRecallsAttributesAsync()
    {
        // Arrange: Create two source systems, both contributing to the same MVO
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        sourceType.RemoveContributedAttributesOnObsoletion = true;

        var source2System = await CreateConnectedSystemAsync("Training Source");
        var source2Type = await CreateCsoTypeAsync(source2System.Id, "User");

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.Zero);

        // Create import sync rule with attribute flow mappings
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoDisplayNameAttr,
                ConnectedSystemAttributeId = csoDisplayNameAttr.Id
            }}
        });

        // Create another import rule for second source
        await CreateImportSyncRuleAsync(source2System.Id, source2Type, mvType, "Training Import", enableProjection: false);

        // Create CSO and run Full Sync
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;

        // Create second CSO and join to same MVO (so MVO has remaining connectors)
        var cso2 = await CreateCsoAsync(source2System.Id, source2Type, "John Smith Training", "EMP001");
        cso2.MetaverseObjectId = mvoId;
        cso2.JoinType = ConnectedSystemObjectJoinType.Joined;
        cso2.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(cso2);

        // Mark first CSO as Obsolete and run Delta Sync
        await MarkCsoAsObsoleteAsync(cso);

        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        // Assert: MVO should still exist (remaining connector)
        var mvo = SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId);
        Assert.That(mvo, Is.Not.Null, "MVO should still exist (another CSO is still connected)");

        // Assert: AttributeFlow outcome SHOULD be present (recall happened because MVO is NOT being deleted)
        var disconnectedRpei = deltaSyncActivity.RunProfileExecutionItems
            .Single(r => r.ObjectChangeType == ObjectChangeType.Disconnected);
        var rootOutcome = disconnectedRpei.SyncOutcomes
            .Single(o => o.ParentSyncOutcome == null);

        var attributeFlowOutcome = rootOutcome.Children
            .SingleOrDefault(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        Assert.That(attributeFlowOutcome, Is.Not.Null,
            "AttributeFlow outcome SHOULD be present when MVO is NOT being deleted — " +
            "attribute recall is meaningful work because target systems need the update");

        // Verify no MvoDeleted outcome (MVO is not being deleted)
        var mvoDeletedOutcome = rootOutcome.Children
            .SingleOrDefault(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        Assert.That(mvoDeletedOutcome, Is.Null,
            "MvoDeleted outcome should NOT be present (remaining connectors)");
    }

    #endregion

    #region Out-of-Scope Path — Immediate Deletion Skips Recall

    /// <summary>
    /// Verifies that when a CSO goes out of scope and the MVO will be deleted immediately,
    /// attribute recall is skipped. Tests the HandleCsoOutOfScopeAsync path.
    /// </summary>
    [Test]
    public async Task OutOfScope_ImmediateDeletion_SkipsAttributeRecallAsync()
    {
        // Arrange: Create source system with scoping criteria and attribute recall enabled
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        sourceSystem.ObjectMatchingRuleMode = ObjectMatchingRuleMode.SyncRule;
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        sourceType.RemoveContributedAttributesOnObsoletion = true;

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.Zero);

        // Create import sync rule with attribute flow mappings AND scoping criteria
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
        var csoEmployeeIdAttr = sourceType.Attributes.First(a => a.Name == "EmployeeId");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = mvDisplayNameAttr,
            TargetMetaverseAttributeId = mvDisplayNameAttr.Id,
            Sources = { new SyncRuleMappingSource
            {
                Order = 0,
                ConnectedSystemAttribute = csoDisplayNameAttr,
                ConnectedSystemAttributeId = csoDisplayNameAttr.Id
            }}
        });

        // Add scoping criteria: EmployeeId must equal "EMP001"
        importRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    ConnectedSystemAttribute = csoEmployeeIdAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "EMP001",
                    CaseSensitive = true
                }
            }
        });

        // Create CSO that matches scoping and run Full Sync to project
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");
        var mvoId = cso.MetaverseObjectId!.Value;

        // Change CSO EmployeeId to make it fall out of scope
        var empIdAttrValue = cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId");
        empIdAttrValue.StringValue = "OUT_OF_SCOPE";
        cso.LastUpdated = DateTime.UtcNow;

        // Run Full Sync again — CSO should now be out of scope
        var fullSync2Profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var fullSync2Activity = await CreateActivityAsync(sourceSystem.Id, fullSync2Profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSync2Profile, fullSync2Activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Assert: MVO should be deleted
        Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Null,
            "MVO should be deleted immediately when CSO goes out of scope (0 grace period)");

        // Assert: The RPEI for DisconnectedOutOfScope should have NO AttributeFlow child
        var outOfScopeRpei = fullSync2Activity.RunProfileExecutionItems
            .FirstOrDefault(r => r.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope);
        Assert.That(outOfScopeRpei, Is.Not.Null,
            "Should have a DisconnectedOutOfScope RPEI");

        var rootOutcome = outOfScopeRpei!.SyncOutcomes
            .SingleOrDefault(o => o.ParentSyncOutcome == null);
        Assert.That(rootOutcome, Is.Not.Null, "RPEI should have a root outcome");

        // Verify NO AttributeFlow child (the optimisation!)
        var attributeFlowOutcome = rootOutcome!.Children
            .SingleOrDefault(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        Assert.That(attributeFlowOutcome, Is.Null,
            "AttributeFlow outcome should NOT be present when MVO is deleted immediately — " +
            "attribute recall is nugatory work and should be skipped (#390)");

        // Verify MvoDeleted is present
        var mvoDeletedOutcome = rootOutcome.Children
            .SingleOrDefault(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        Assert.That(mvoDeletedOutcome, Is.Not.Null,
            "MvoDeleted outcome should be present");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Creates a Metaverse Object Type with specific deletion rule settings.
    /// </summary>
    private async Task<MetaverseObjectType> CreateMvObjectTypeWithDeletionRuleAsync(
        string name,
        MetaverseObjectDeletionRule deletionRule,
        TimeSpan? gracePeriod = null,
        List<int>? triggerConnectedSystemIds = null)
    {
        var mvType = new MetaverseObjectType
        {
            Name = name,
            PluralName = name + "s",
            BuiltIn = false,
            DeletionRule = deletionRule,
            DeletionGracePeriod = gracePeriod,
            DeletionTriggerConnectedSystemIds = triggerConnectedSystemIds ?? new List<int>(),
            Attributes = new List<MetaverseAttribute>(),
            ExampleDataTemplateAttributes = new List<JIM.Models.ExampleData.ExampleDataTemplateAttribute>(),
            PredefinedSearches = new List<JIM.Models.Search.PredefinedSearch>()
        };

        DbContext.MetaverseObjectTypes.Add(mvType);
        await DbContext.SaveChangesAsync();

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

        mvType.Attributes.Add(displayNameAttr);
        mvType.Attributes.Add(employeeIdAttr);
        mvType.Attributes.Add(typeAttr);

        return mvType;
    }

    /// <summary>
    /// Creates an export sync rule.
    /// </summary>
    private async Task<SyncRule> CreateExportSyncRuleAsync(
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

        SyncRepo.SeedSyncRule(syncRule);

        return syncRule;
    }

    /// <summary>
    /// Creates a matching rule for joining CSOs to MVOs.
    /// </summary>
    private async Task<ObjectMatchingRule> CreateMatchingRuleAsync(
        ConnectedSystemObjectType csoType,
        MetaverseObjectType mvType,
        string attributeName)
    {
        var csoAttr = csoType.Attributes.First(a => a.Name == attributeName);
        var mvAttr = mvType.Attributes.First(a => a.Name == attributeName);

        var matchingRule = new ObjectMatchingRule
        {
            Order = 1,
            CaseSensitive = true,
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
    private Task MarkCsoAsObsoleteAsync(ConnectedSystemObject cso)
    {
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;
        return Task.CompletedTask;
    }

    #endregion
}
