// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// The release-blocking fidelity pairing for the Sync Preview Engine (#288, PRD requirement 9): a preview's
/// speculative outcome tree must have the same shape as the tree the real synchronisation then records for
/// the same object over the same data. Each test previews first (which must not disturb the data: the
/// pairing itself proves it, because the real sync then runs over whatever state the preview left) and
/// diffs the preview tree against the real tree mapped through the one shared mapping,
/// <see cref="SyncOutcomeNode.FromSyncOutcome"/> (PRD decision D4).
///
/// The shape compared is (OutcomeType, DetailCount, child count) per node in sibling order. Entity ids and
/// detail messages are deliberately excluded: a preview persists nothing, so it has no Pending Export or
/// provisioning CSO ids to carry.
/// </summary>
[TestFixture]
public class SyncPreviewFidelityTests : WorkflowTestBase
{
    /// <summary>
    /// The flagship chain: an unjoined CSO that would project, flow a display name, provision a target
    /// object and stage its Pending Export. Preview tree and real tree must match shape node for node:
    /// Projected -> Attribute Flow -> Provisioned -> Pending Export.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ProjectionWithProvisioningExport_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        // Arrange - source system with a projecting import rule flowing DisplayName
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");

        // Flow into a correctly-named built-in Display Name attribute so the Identity resolves a name,
        // matching the real naming path (see ProjectedOutcomeDescriptionTests for the rationale).
        var mvDisplayNameAttr = new MetaverseAttribute
        {
            Name = Constants.BuiltInAttributes.DisplayName,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType],
            PredefinedSearchAttributes = []
        };
        DbContext.MetaverseAttributes.Add(mvDisplayNameAttr);
        mvType.Attributes.Add(mvDisplayNameAttr);
        await DbContext.SaveChangesAsync();

        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
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

        // Target system with a provisioning export rule flowing the same attribute back out
        var targetSystem = await CreateConnectedSystemAsync("AD Target");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "user");
        var exportRule = await CreateExportSyncRuleAsync(targetSystem.Id, targetType, mvType, "AD Export");
        var targetDisplayNameAttr = targetType.Attributes.First(a => a.Name == "DisplayName");
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

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        // Act 1 - preview, BEFORE the real sync, over identical data
        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(sourceSystem.Id, cso.Id);

        // Act 2 - the real synchronisation over the same (undisturbed) data
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Assert - the real tree, mapped through the one shared mapping, matches the preview tree's shape
        var realTree = MapRealOutcomeTree(fullSyncActivity);
        Assert.That(realTree, Is.Not.Empty, "The real synchronisation must have recorded an outcome tree to pair against");
        Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(realTree)),
            "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");

        // The preview's inbound summary and outbound counters must agree with what really happened
        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Inbound!.WouldProject, Is.True);
            Assert.That(preview.Outbound.ObjectsToCreate, Is.EqualTo(1),
                "The preview said one target object would be created, and the real sync provisioned one");
            Assert.That(preview.OutcomeTree[0].TargetEntityDescription, Is.EqualTo(realTree[0].TargetEntityDescription),
                "Both trees must name the Identity identically at the root");
        }
    }

    /// <summary>
    /// The steady-state chain: a joined CSO whose source value changed, with no export rules. Preview tree
    /// and real tree must both be a single Attribute Flow root.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_JoinedCsoWithChangedValue_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        // Arrange - projecting import rule with a DisplayName flow; first sync establishes the join
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeAsync("Person");
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
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

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var firstProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 1", ConnectedSystemRunType.FullSynchronisation);
        var firstActivity = await CreateActivityAsync(sourceSystem.Id, firstProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, firstProfile, firstActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "The first sync must have joined the CSO");

        // The source value changes
        cso.AttributeValues.Single(av => av.AttributeId == csoDisplayNameAttr.Id).StringValue = "John Smith-Jones";

        // Act 1 - preview the changed object against the live joined state
        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(sourceSystem.Id, cso.Id);

        // Act 2 - the real second synchronisation
        var secondProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        var secondActivity = await CreateActivityAsync(sourceSystem.Id, secondProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, secondProfile, secondActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Assert
        var realTree = MapRealOutcomeTree(secondActivity);
        Assert.That(realTree, Is.Not.Empty, "The second sync must have recorded an Attribute Flow outcome");
        using (Assert.EnterMultipleScope())
        {
            Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(DescribeTree(realTree)),
                "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");
            Assert.That(preview.Inbound!.AlreadyJoinedMetaverseObjectId, Is.EqualTo(cso.MetaverseObjectId));
            Assert.That(preview.Inbound!.AttributeFlowChanges.Any(c => c.IsAddition && c.Value == "John Smith-Jones"), Is.True);
        }
    }

    #region Destructive Cascade (#288 Phase 1 of the Sync Preview Surface plan)

    /// <summary>
    /// The flagship cascade chain: a joined source object falls out of scope, its Metaverse Object's
    /// authoritative source triggers immediate deletion despite two provisioned targets remaining joined,
    /// and both targets' export Synchronisation Rules stage a delete. Preview tree and real tree must match
    /// shape node for node: DisconnectedOutOfScope -> MvoDeleted -> {DeprovisionQueued, DeprovisionQueued}.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithImmediateDeletionAndTwoDeprovisions_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        // Arrange - a source system whose CSO type carries a scoping import rule, and two target systems
        // whose export rules provision and, on Metaverse Object deletion, stage a delete (#655).
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var targetSystem1 = await CreateConnectedSystemAsync("AD Target 1");
        var targetType1 = await CreateCsoTypeAsync(targetSystem1.Id, "user");
        var targetSystem2 = await CreateConnectedSystemAsync("AD Target 2");
        var targetType2 = await CreateCsoTypeAsync(targetSystem2.Id, "user");

        // WhenAuthoritativeSourceDisconnected with the source as the (only) trigger and zero grace period:
        // deletion fires the moment the source disconnects, even though both targets remain joined
        // (WhenLastConnectorDisconnected cannot fire here - the targets are still connectors).
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.Zero,
            triggerConnectedSystemIds: [sourceSystem.Id]);

        await CreateScopedImportSyncRuleAsync(sourceSystem, sourceType, mvType);
        await CreateExportSyncRuleAsync(targetSystem1.Id, targetType1, mvType, "AD Export 1",
            deprovisionAction: OutboundDeprovisionAction.Delete);
        await CreateExportSyncRuleAsync(targetSystem2.Id, targetType2, mvType, "AD Export 2",
            deprovisionAction: OutboundDeprovisionAction.Delete);

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        // Full Sync 1 - projects the source object and provisions both targets (PendingProvisioning, joined).
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "The first sync must have joined the CSO");
        var mvoId = cso.MetaverseObjectId!.Value;
        Assert.That(SyncRepo.ConnectedSystemObjects.Values.Count(c => c.MetaverseObjectId == mvoId), Is.EqualTo(3),
            "Both targets must have been provisioned and joined to the same Metaverse Object");

        // The targets are live accounts: their provisioning was exported and confirmed, so the Create Pending
        // Exports are gone and the CSOs are Normal. (Left Pending Provisioning with unsent Creates, the deletion
        // cancels the provisioning instead of deprovisioning; the sibling test below pins that.)
        foreach (var targetCso in SyncRepo.ConnectedSystemObjects.Values.Where(c => c.MetaverseObjectId == mvoId && c.Id != cso.Id))
            targetCso.Status = ConnectedSystemObjectStatus.Normal;
        SyncRepo.ClearAllPendingExports();

        // Put the CSO out of scope.
        var empIdAttrValue = cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId");
        empIdAttrValue.StringValue = "OUT_OF_SCOPE";
        cso.LastUpdated = DateTime.UtcNow;

        // Act 1 - preview, BEFORE the real sync, over identical data
        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(sourceSystem.Id, cso.Id);

        // Act 2 - the real synchronisation over the same (undisturbed) data
        var fullSync2Profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var fullSync2Activity = await CreateActivityAsync(sourceSystem.Id, fullSync2Profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSync2Profile, fullSync2Activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Null,
            "MVO should be deleted immediately (zero grace period, authoritative source disconnected)");

        // Assert - the real tree, mapped through the one shared mapping, matches the preview tree's shape
        var realTree = MapRealOutcomeTree(fullSync2Activity);
        var describedReal = DescribeTree(realTree);
        var describedPreview = DescribeTree(preview.OutcomeTree);
        Assert.That(describedReal, Does.StartWith("DisconnectedOutOfScope"));
        Assert.That(describedReal, Does.Contain("MvoDeleted"));
        Assert.That(describedReal, Does.Contain("DeprovisionQueued"), "Report: real tree shape -> " + describedReal);
        Assert.That(describedPreview, Is.EqualTo(describedReal),
            "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9). " +
            $"Preview: {describedPreview} | Real: {describedReal}");

        // Two delete Pending Exports really were staged, one per target, matching the preview's proposed exports
        var realDeleteExports = SyncRepo.PendingExports.Values.Count(pe => pe.ChangeType == PendingExportChangeType.Delete);
        Assert.That(realDeleteExports, Is.EqualTo(2));
        Assert.That(preview.Outbound.ProposedExports.Count(pe => pe.ChangeType == PendingExportChangeType.Delete), Is.EqualTo(2));
    }

    /// <summary>
    /// The same cascade over targets whose provisioning was never exported (Pending Provisioning, each carrying
    /// an unsent Create). Nothing exists in the target systems, so the deletion cancels the provisioning rather
    /// than deprovisioning: no Delete is staged, no Deprovision Queued node is recorded, and the CSOs are
    /// removed. The preview must say the same: an identical tree, no proposed Deletes, and one
    /// <see cref="SyncPreviewMessageCode.DownstreamProvisioningCancelled"/> warning per target.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithImmediateDeletionOfNeverExportedTargets_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var targetSystem1 = await CreateConnectedSystemAsync("AD Target 1");
        var targetType1 = await CreateCsoTypeAsync(targetSystem1.Id, "user");
        var targetSystem2 = await CreateConnectedSystemAsync("AD Target 2");
        var targetType2 = await CreateCsoTypeAsync(targetSystem2.Id, "user");

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.Zero,
            triggerConnectedSystemIds: [sourceSystem.Id]);

        await CreateScopedImportSyncRuleAsync(sourceSystem, sourceType, mvType);
        await CreateExportSyncRuleAsync(targetSystem1.Id, targetType1, mvType, "AD Export 1",
            deprovisionAction: OutboundDeprovisionAction.Delete);
        await CreateExportSyncRuleAsync(targetSystem2.Id, targetType2, mvType, "AD Export 2",
            deprovisionAction: OutboundDeprovisionAction.Disconnect);

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        // Full Sync 1 - projects the source object and provisions both targets. No export runs, so both stay
        // Pending Provisioning with an unsent Create.
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;
        var targetCsoIds = SyncRepo.ConnectedSystemObjects.Values
            .Where(c => c.MetaverseObjectId == mvoId && c.Id != cso.Id)
            .Select(c => c.Id)
            .ToList();
        Assert.That(targetCsoIds, Has.Count.EqualTo(2), "Both targets must have been provisioned");

        // Put the CSO out of scope.
        var empIdAttrValue = cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId");
        empIdAttrValue.StringValue = "OUT_OF_SCOPE";
        cso.LastUpdated = DateTime.UtcNow;

        // Act 1 - preview, BEFORE the real sync, over identical data
        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(sourceSystem.Id, cso.Id);

        // Act 2 - the real synchronisation over the same (undisturbed) data
        var fullSync2Profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var fullSync2Activity = await CreateActivityAsync(sourceSystem.Id, fullSync2Profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSync2Profile, fullSync2Activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        var describedReal = DescribeTree(MapRealOutcomeTree(fullSync2Activity));
        var describedPreview = DescribeTree(preview.OutcomeTree);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Null, "The Metaverse Object should be deleted immediately");
            Assert.That(describedReal, Does.Contain("MvoDeleted"));
            Assert.That(describedReal, Does.Not.Contain("DeprovisionQueued"), "Nothing exists in the targets, so nothing is deprovisioned");
            Assert.That(describedPreview, Is.EqualTo(describedReal),
                $"The preview's outcome tree must match the real run's. Preview: {describedPreview} | Real: {describedReal}");

            Assert.That(SyncRepo.PendingExports.Values, Is.Empty, "The unsent Creates are cancelled and no Delete is staged");
            Assert.That(targetCsoIds.Any(SyncRepo.ConnectedSystemObjects.ContainsKey), Is.False, "The never-provisioned CSOs are removed");

            Assert.That(preview.Outbound.ProposedExports, Is.Empty);
            Assert.That(preview.Warnings.Count(w => w.Code == SyncPreviewMessageCode.DownstreamProvisioningCancelled), Is.EqualTo(2),
                "One warning per cancelled target, whichever deprovisioning action its rule carries");
            Assert.That(preview.Warnings.Any(w => w.Code == SyncPreviewMessageCode.DownstreamDisconnectOnly), Is.False);
        }
    }

    /// <summary>
    /// The same authoritative-source trigger, with a grace period: the deletion is scheduled, not
    /// immediate, so nothing downstream is staged yet (a scheduled deletion stages nothing). Preview tree
    /// and real tree must both be DisconnectedOutOfScope -> MvoDeletionScheduled, with no further children.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithScheduledDeletion_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var targetSystem = await CreateConnectedSystemAsync("AD Target");
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "user");

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
            gracePeriod: TimeSpan.FromDays(7),
            triggerConnectedSystemIds: [sourceSystem.Id]);

        await CreateScopedImportSyncRuleAsync(sourceSystem, sourceType, mvType);
        await CreateExportSyncRuleAsync(targetSystem.Id, targetType, mvType, "AD Export",
            deprovisionAction: OutboundDeprovisionAction.Delete);

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;

        var empIdAttrValue = cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId");
        empIdAttrValue.StringValue = "OUT_OF_SCOPE";
        cso.LastUpdated = DateTime.UtcNow;

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(sourceSystem.Id, cso.Id);

        var fullSync2Profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var fullSync2Activity = await CreateActivityAsync(sourceSystem.Id, fullSync2Profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSync2Profile, fullSync2Activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Not.Null,
            "MVO should still exist during the deletion grace period");

        var realTree = MapRealOutcomeTree(fullSync2Activity);
        var describedReal = DescribeTree(realTree);
        Assert.That(describedReal, Does.Contain("MvoDeletionScheduled"));
        Assert.That(describedReal, Does.Not.Contain("DeprovisionQueued"),
            "A scheduled deletion stages nothing downstream yet");
        Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(describedReal),
            "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");
        Assert.That(preview.Outbound.ProposedExports, Is.Empty,
            "Nothing would be staged yet for a scheduled (not immediate) deletion");
    }

    /// <summary>
    /// Another connector remains joined to the same Metaverse Object (a second Connected System Object on
    /// the disconnecting object's own system): under WhenLastConnectorDisconnected the deletion rule finds a
    /// remaining connector and does not fire, so the cascade stops at the disconnect root. Preview tree and
    /// real tree must both be a bare DisconnectedOutOfScope root.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithAnotherConnectorRemaining_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person", MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, gracePeriod: TimeSpan.Zero);

        await CreateScopedImportSyncRuleAsync(sourceSystem, sourceType, mvType);

        // One object projects; a second is then joined directly to the same Metaverse Object (simulating a
        // matched pair on the same system), so it remains a connector when the first falls out of scope.
        var cso1 = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");
        var cso2 = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith Two", "EMP002");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso1 = await ReloadEntityAsync(cso1);
        Assert.That(cso1.MetaverseObjectId, Is.Not.Null, "The first object must have projected");
        var mvoId = cso1.MetaverseObjectId!.Value;

        cso2.MetaverseObjectId = mvoId;
        cso2.JoinType = ConnectedSystemObjectJoinType.Joined;
        cso2.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(cso2);
        cso2 = await ReloadEntityAsync(cso2);
        Assert.That(cso2.MetaverseObjectId, Is.EqualTo(mvoId), "The second object must be joined to the same Metaverse Object");

        var empIdAttrValue = cso1.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId");
        empIdAttrValue.StringValue = "OUT_OF_SCOPE";
        cso1.LastUpdated = DateTime.UtcNow;

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(sourceSystem.Id, cso1.Id);

        var fullSync2Profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var fullSync2Activity = await CreateActivityAsync(sourceSystem.Id, fullSync2Profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSync2Profile, fullSync2Activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Not.Null, "MVO must survive: cso2 is still a connector");

        var realTree = MapRealOutcomeTree(fullSync2Activity);
        var describedReal = DescribeTree(realTree);
        Assert.That(describedReal, Does.Not.Contain("MvoDeleted"));
        Assert.That(describedReal, Does.Not.Contain("MvoDeletionScheduled"));
        Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(describedReal),
            "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");
    }

    /// <summary>
    /// Deletion Rule Manual: the deletion rule never fires regardless of remaining connectors, so the
    /// cascade stops at the disconnect root exactly as the "another connector remains" case does.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithManualDeletionRule_TreeMatchesTheRealSyncOutcomeTreeAsync()
    {
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync("Person", MetaverseObjectDeletionRule.Manual);

        await CreateScopedImportSyncRuleAsync(sourceSystem, sourceType, mvType);

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;

        var empIdAttrValue = cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId");
        empIdAttrValue.StringValue = "OUT_OF_SCOPE";
        cso.LastUpdated = DateTime.UtcNow;

        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(sourceSystem.Id, cso.Id);

        var fullSync2Profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var fullSync2Activity = await CreateActivityAsync(sourceSystem.Id, fullSync2Profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSync2Profile, fullSync2Activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Not.Null, "Manual Deletion Rule must never delete automatically");

        var realTree = MapRealOutcomeTree(fullSync2Activity);
        var describedReal = DescribeTree(realTree);
        Assert.That(describedReal, Does.Not.Contain("MvoDeleted"));
        Assert.That(DescribeTree(preview.OutcomeTree), Is.EqualTo(describedReal),
            "The preview's outcome tree must have the same shape as the tree the real synchronisation recorded (PRD requirement 9)");
    }

    /// <summary>
    /// InboundOutOfScopeAction.RemainJoined: the join is preserved, so there is nothing to cascade. The
    /// preview keeps today's behaviour (the OutOfScope warning, an empty tree); the real run's join is left
    /// intact and records no DisconnectedOutOfScope outcome anywhere on the Activity.
    /// </summary>
    [Test]
    public async Task PreviewSyncForCsoAsync_ScopeExitWithRemainJoinedAction_RecordsNoDisconnectedOutOfScopeEitherWayAsync()
    {
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person", MetaverseObjectDeletionRule.WhenLastConnectorDisconnected, gracePeriod: TimeSpan.Zero);

        var importRule = await CreateScopedImportSyncRuleAsync(sourceSystem, sourceType, mvType);
        importRule.InboundOutOfScopeAction = InboundOutOfScopeAction.RemainJoined;

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;

        var empIdAttrValue = cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId");
        empIdAttrValue.StringValue = "OUT_OF_SCOPE";
        cso.LastUpdated = DateTime.UtcNow;

        // Act 1 - preview
        var preview = await Jim.SyncPreview.PreviewSyncForCsoAsync(sourceSystem.Id, cso.Id);

        // Act 2 - the real synchronisation
        var fullSync2Profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var fullSync2Activity = await CreateActivityAsync(sourceSystem.Id, fullSync2Profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSync2Profile, fullSync2Activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        using (Assert.EnterMultipleScope())
        {
            Assert.That(preview.Warnings.Any(w => w.Code == SyncPreviewMessageCode.OutOfScope), Is.True);
            Assert.That(preview.OutcomeTree, Is.Empty, "RemainJoined keeps the join intact: nothing to cascade");

            var reloadedCso = await ReloadEntityAsync(cso);
            Assert.That(reloadedCso.MetaverseObjectId, Is.EqualTo(mvoId), "The real run must keep the join intact too");
            Assert.That(fullSync2Activity.RunProfileExecutionItems
                    .SelectMany(rpei => rpei.SyncOutcomes)
                    .Any(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope),
                Is.False, "The real run must record no DisconnectedOutOfScope outcome when the join is retained");
        }
    }

    #endregion

    #region Destructive Cascade helpers

    /// <summary>
    /// Creates a Metaverse Object Type with specific deletion rule settings. The DisplayName attribute is
    /// renamed to the built-in "Display Name" attribute name so <see cref="MetaverseObject.DisplayName"/>
    /// (and therefore <c>NameOrId</c>/<c>Name</c>) resolves it, matching production.
    /// </summary>
    private async Task<MetaverseObjectType> CreateMvObjectTypeWithDeletionRuleAsync(
        string name,
        MetaverseObjectDeletionRule deletionRule,
        TimeSpan? gracePeriod = null,
        List<int>? triggerConnectedSystemIds = null,
        AuthoritativeSourceTriggerMode triggerMode = AuthoritativeSourceTriggerMode.SpecificSourcesDisconnect)
    {
        var mvType = await CreateMvObjectTypeAsync(name);
        mvType.DeletionRule = deletionRule;
        mvType.DeletionGracePeriod = gracePeriod;
        mvType.DeletionTriggerConnectedSystemIds = triggerConnectedSystemIds ?? [];
        mvType.DeletionTriggerMode = triggerMode;
        mvType.Attributes.First(a => a.Name == "DisplayName").Name = Constants.BuiltInAttributes.DisplayName;
        await DbContext.SaveChangesAsync();
        return mvType;
    }

    /// <summary>
    /// Creates an import Synchronisation Rule with a DisplayName Attribute Flow mapping.
    /// </summary>
    private async Task<SyncRule> CreateImportSyncRuleWithDisplayNameFlowAsync(
        ConnectedSystem sourceSystem,
        ConnectedSystemObjectType sourceType,
        MetaverseObjectType mvType)
    {
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");
        var csoDisplayNameAttr = sourceType.Attributes.First(a => a.Name == "DisplayName");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == Constants.BuiltInAttributes.DisplayName);
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
        return importRule;
    }

    /// <summary>
    /// Creates an import Synchronisation Rule with a DisplayName Attribute Flow mapping and scoping
    /// criteria requiring EmployeeId not to equal "OUT_OF_SCOPE" (rather than pinning to one exact value),
    /// so more than one Connected System Object can independently satisfy the same rule.
    /// </summary>
    private async Task<SyncRule> CreateScopedImportSyncRuleAsync(
        ConnectedSystem sourceSystem,
        ConnectedSystemObjectType sourceType,
        MetaverseObjectType mvType)
    {
        var importRule = await CreateImportSyncRuleWithDisplayNameFlowAsync(sourceSystem, sourceType, mvType);
        var csoEmployeeIdAttr = sourceType.Attributes.First(a => a.Name == "EmployeeId");
        importRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    ConnectedSystemAttribute = csoEmployeeIdAttr,
                    ComparisonType = SearchComparisonType.NotEquals,
                    StringValue = "OUT_OF_SCOPE",
                    CaseSensitive = true
                }
            }
        });
        return importRule;
    }

    #endregion

    #region helpers

    /// <summary>
    /// Maps the real synchronisation's recorded outcome roots for the run through the shared
    /// <see cref="SyncOutcomeNode.FromSyncOutcome"/> mapping, in root ordinal order.
    /// </summary>
    private static List<SyncOutcomeNode> MapRealOutcomeTree(Activity activity)
    {
        return activity.RunProfileExecutionItems
            .SelectMany(rpei => rpei.SyncOutcomes)
            .Where(o => o.ParentSyncOutcome == null && !o.ParentSyncOutcomeId.HasValue)
            .OrderBy(o => o.Ordinal)
            .Select(SyncOutcomeNode.FromSyncOutcome)
            .ToList();
    }

    /// <summary>
    /// Flattens a tree into a comparable shape description: one line per node in sibling order, carrying
    /// depth, outcome type, detail count, staged change kind and child count. StagedChangeType is included
    /// so a preview's Export queued node is proven to carry the same staged kind (#1561 follow-up) the real
    /// synchronisation would record, not just the same outcome type and count.
    /// </summary>
    private static string DescribeTree(IEnumerable<SyncOutcomeNode> nodes, int depth = 0)
    {
        var lines = new List<string>();
        foreach (var node in nodes)
        {
            lines.Add($"{new string(' ', depth * 2)}{node.OutcomeType} (count: {node.DetailCount?.ToString() ?? "-"}, " +
                $"staged: {node.StagedChangeType?.ToString() ?? "-"}, children: {node.Children.Count})");
            if (node.Children.Count > 0)
                lines.Add(DescribeTree(node.Children.OrderBy(c => c.Ordinal), depth + 1));
        }
        return string.Join(Environment.NewLine, lines);
    }

    #endregion
}
