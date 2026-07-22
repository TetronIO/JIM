// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

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
/// Tests for GitHub issue #1086: surface deleted Identity details on MVO Deleted outcomes.
///
/// MvoDeleted and MvoDeletionScheduled causality tree outcomes must carry the deleted (or
/// scheduled-for-deletion) Metaverse Object's id and display name snapshot, captured before
/// deletion, plus a human-readable Deletion Rule reason in the detail message (combined with
/// the existing grace-period text for scheduled deletions). The out-of-scope disconnection
/// path previously left all of these null because the CSO-MVO join was broken before the
/// outcome nodes were built, producing bare "MVO Deleted" labels in the Causality Tree.
/// </summary>
[TestFixture]
public class MvoDeletionOutcomeDetailTests : WorkflowTestBase
{
    #region Out-of-Scope Path

    /// <summary>
    /// Reproduces the bare-node case from #1086: a CSO falls out of scope, the Metaverse Object is
    /// deleted immediately (0 grace period), and the MvoDeleted child outcome must carry the deleted
    /// Metaverse Object's id, display name snapshot, and a Deletion Rule reason.
    /// </summary>
    [Test]
    public async Task HandleCsoOutOfScope_ImmediateMvoDeletion_MvoDeletedOutcomeCarriesDeletedIdentityDetailsAsync()
    {
        // Arrange: source system with scoping criteria and an immediate deletion rule
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        sourceType.RemoveContributedAttributesOnObsoletion = true;

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.Zero);

        var importRule = await CreateScopedImportSyncRuleAsync(sourceSystem, sourceType, mvType);
        Assert.That(importRule, Is.Not.Null);

        // Create a CSO in scope and run a Full Sync so it projects and flows attributes
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");
        var mvoId = cso.MetaverseObjectId!.Value;

        // Change the CSO's EmployeeId so it falls out of scope
        var empIdAttrValue = cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId");
        empIdAttrValue.StringValue = "OUT_OF_SCOPE";
        cso.LastUpdated = DateTime.UtcNow;

        // Act: run a second Full Sync; the CSO is now out of scope and the MVO is deleted immediately
        var fullSync2Profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var fullSync2Activity = await CreateActivityAsync(sourceSystem.Id, fullSync2Profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSync2Profile, fullSync2Activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Null,
            "MVO should be deleted immediately (0 grace period)");

        // Assert: the MvoDeleted child outcome carries the deleted Identity's details
        var mvoDeletedOutcome = GetDeletionFateOutcome(fullSync2Activity,
            ObjectChangeType.DisconnectedOutOfScope, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        Assert.That(mvoDeletedOutcome.TargetEntityId, Is.EqualTo(mvoId),
            "The MvoDeleted outcome must record the deleted Metaverse Object's id");
        Assert.That(mvoDeletedOutcome.TargetEntityDescription, Is.EqualTo("John Smith"),
            "The MvoDeleted outcome must record a display name snapshot captured before deletion");
        Assert.That(mvoDeletedOutcome.DetailMessage, Is.EqualTo("Deletion Rule: last connector disconnected"),
            "The MvoDeleted outcome must record the Deletion Rule reason");
    }

    /// <summary>
    /// Verifies the DisconnectedOutOfScope root outcome also carries the disconnected Metaverse Object's
    /// id and display name snapshot when the join was broken during out-of-scope processing.
    /// </summary>
    [Test]
    public async Task HandleCsoOutOfScope_ImmediateMvoDeletion_RootOutcomeCarriesDisconnectedMvoDetailsAsync()
    {
        // Arrange
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        sourceType.RemoveContributedAttributesOnObsoletion = true;

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.Zero);

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

        // Act
        var fullSync2Profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var fullSync2Activity = await CreateActivityAsync(sourceSystem.Id, fullSync2Profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSync2Profile, fullSync2Activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Assert: the root Out of Scope outcome shows which Identity was disconnected
        var outOfScopeRpei = fullSync2Activity.RunProfileExecutionItems
            .Single(r => r.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope);
        var rootOutcome = outOfScopeRpei.SyncOutcomes
            .Single(o => o.ParentSyncOutcome == null);
        Assert.That(rootOutcome.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope));
        Assert.That(rootOutcome.TargetEntityId, Is.EqualTo(mvoId),
            "The DisconnectedOutOfScope root outcome must record the disconnected Metaverse Object's id");
        Assert.That(rootOutcome.TargetEntityDescription, Is.EqualTo("John Smith"),
            "The DisconnectedOutOfScope root outcome must record a display name snapshot");
    }

    /// <summary>
    /// Verifies that when a CSO falls out of scope and the Metaverse Object deletion is scheduled
    /// (grace period configured), the MvoDeletionScheduled outcome carries the Metaverse Object's
    /// details plus a detail message combining the Deletion Rule reason and the grace period.
    /// </summary>
    [Test]
    public async Task HandleCsoOutOfScope_ScheduledMvoDeletion_OutcomeCombinesReasonAndGracePeriodAsync()
    {
        // Arrange: source system with scoping criteria and a 7 day deletion grace period
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        sourceType.RemoveContributedAttributesOnObsoletion = true;

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.FromDays(7));

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

        // Act
        var fullSync2Profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var fullSync2Activity = await CreateActivityAsync(sourceSystem.Id, fullSync2Profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSync2Profile, fullSync2Activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Not.Null,
            "MVO should still exist during the deletion grace period");

        // Assert: the MvoDeletionScheduled outcome carries details, reason and grace period
        var scheduledOutcome = GetDeletionFateOutcome(fullSync2Activity,
            ObjectChangeType.DisconnectedOutOfScope, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled);
        Assert.That(scheduledOutcome.TargetEntityId, Is.EqualTo(mvoId),
            "The MvoDeletionScheduled outcome must record the Metaverse Object's id");
        Assert.That(scheduledOutcome.TargetEntityDescription, Is.EqualTo("John Smith"),
            "The MvoDeletionScheduled outcome must record a display name snapshot");
        Assert.That(scheduledOutcome.DetailMessage,
            Is.EqualTo("Deletion Rule: last connector disconnected. Grace period: 7 days"),
            "The MvoDeletionScheduled outcome must combine the Deletion Rule reason with the grace period");
    }

    #endregion

    #region Obsoletion Path

    /// <summary>
    /// Verifies that when an obsolete CSO's disconnection triggers immediate Metaverse Object deletion,
    /// the MvoDeleted outcome carries the deleted Identity's details and the Deletion Rule reason.
    /// </summary>
    [Test]
    public async Task ProcessObsoleteConnectedSystemObject_ImmediateMvoDeletion_MvoDeletedOutcomeCarriesReasonAsync()
    {
        // Arrange: source system with an immediate deletion rule
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        sourceType.RemoveContributedAttributesOnObsoletion = true;

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.Zero);

        await CreateImportSyncRuleWithDisplayNameFlowAsync(sourceSystem, sourceType, mvType);

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;

        // Mark the CSO as Obsolete and run a Delta Sync
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;

        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Null,
            "MVO should be deleted immediately (0 grace period)");

        // Assert: the MvoDeleted outcome carries the deleted Identity's details and reason
        var mvoDeletedOutcome = GetDeletionFateOutcome(deltaSyncActivity,
            ObjectChangeType.Disconnected, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        Assert.That(mvoDeletedOutcome.TargetEntityId, Is.EqualTo(mvoId),
            "The MvoDeleted outcome must record the deleted Metaverse Object's id");
        Assert.That(mvoDeletedOutcome.TargetEntityDescription, Is.EqualTo("John Smith"),
            "The MvoDeleted outcome must record a display name snapshot captured before deletion");
        Assert.That(mvoDeletedOutcome.DetailMessage, Is.EqualTo("Deletion Rule: last connector disconnected"),
            "The MvoDeleted outcome must record the Deletion Rule reason");
    }

    /// <summary>
    /// Verifies that when an obsolete CSO's disconnection schedules Metaverse Object deletion
    /// (grace period configured), the MvoDeletionScheduled outcome's detail message combines
    /// the Deletion Rule reason with the existing grace-period text.
    /// </summary>
    [Test]
    public async Task ProcessObsoleteConnectedSystemObject_ScheduledMvoDeletion_DetailMessageCombinesReasonAndGracePeriodAsync()
    {
        // Arrange: source system with a 7 day deletion grace period
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        sourceType.RemoveContributedAttributesOnObsoletion = true;

        var mvType = await CreateMvObjectTypeWithDeletionRuleAsync(
            "Person",
            MetaverseObjectDeletionRule.WhenLastConnectorDisconnected,
            gracePeriod: TimeSpan.FromDays(7));

        await CreateImportSyncRuleWithDisplayNameFlowAsync(sourceSystem, sourceType, mvType);

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        var mvoId = cso.MetaverseObjectId!.Value;

        // Mark the CSO as Obsolete and run a Delta Sync
        cso.Status = ConnectedSystemObjectStatus.Obsolete;
        cso.LastUpdated = DateTime.UtcNow;

        var deltaSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Delta Sync", ConnectedSystemRunType.DeltaSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var deltaSyncActivity = await CreateActivityAsync(sourceSystem.Id, deltaSyncProfile, ConnectedSystemRunType.DeltaSynchronisation);
        await new SyncDeltaSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, deltaSyncProfile, deltaSyncActivity, new CancellationTokenSource())
            .PerformDeltaSyncAsync();

        Assert.That(SyncRepo.MetaverseObjects.GetValueOrDefault(mvoId), Is.Not.Null,
            "MVO should still exist during the deletion grace period");

        // Assert: the MvoDeletionScheduled outcome combines reason and grace period
        var scheduledOutcome = GetDeletionFateOutcome(deltaSyncActivity,
            ObjectChangeType.Disconnected, ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled);
        Assert.That(scheduledOutcome.TargetEntityId, Is.EqualTo(mvoId),
            "The MvoDeletionScheduled outcome must record the Metaverse Object's id");
        Assert.That(scheduledOutcome.TargetEntityDescription, Is.EqualTo("John Smith"),
            "The MvoDeletionScheduled outcome must record a display name snapshot");
        Assert.That(scheduledOutcome.DetailMessage,
            Is.EqualTo("Deletion Rule: last connector disconnected. Grace period: 7 days"),
            "The MvoDeletionScheduled outcome must combine the Deletion Rule reason with the grace period");
    }

    #endregion

    #region Helper Methods

    /// <summary>
    /// Finds the single deletion-fate child outcome of the given type on the activity's RPEI
    /// with the specified object change type, asserting the RPEI and outcome exist.
    /// </summary>
    private static ActivityRunProfileExecutionItemSyncOutcome GetDeletionFateOutcome(
        Activity activity,
        ObjectChangeType rpeiChangeType,
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        var rpei = activity.RunProfileExecutionItems
            .SingleOrDefault(r => r.ObjectChangeType == rpeiChangeType);
        Assert.That(rpei, Is.Not.Null, $"Should have an RPEI with ObjectChangeType {rpeiChangeType}");

        var rootOutcome = rpei!.SyncOutcomes
            .SingleOrDefault(o => o.ParentSyncOutcome == null);
        Assert.That(rootOutcome, Is.Not.Null, "RPEI should have a root outcome");

        var outcome = rootOutcome!.Children
            .SingleOrDefault(c => c.OutcomeType == outcomeType);
        Assert.That(outcome, Is.Not.Null, $"Root outcome should have a {outcomeType} child outcome");
        return outcome!;
    }

    /// <summary>
    /// Creates a Metaverse Object Type with specific deletion rule settings. The display name
    /// attribute is renamed to the built-in "Display Name" attribute name so that
    /// <see cref="MetaverseObject.DisplayName"/> resolves it, matching production.
    /// </summary>
    private async Task<MetaverseObjectType> CreateMvObjectTypeWithDeletionRuleAsync(
        string name,
        MetaverseObjectDeletionRule deletionRule,
        TimeSpan? gracePeriod = null)
    {
        var mvType = await CreateMvObjectTypeAsync(name);
        mvType.DeletionRule = deletionRule;
        mvType.DeletionGracePeriod = gracePeriod;
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
    /// Creates an import Synchronisation Rule with a DisplayName Attribute Flow mapping and
    /// scoping criteria requiring EmployeeId to equal "EMP001".
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
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "EMP001",
                    CaseSensitive = true
                }
            }
        });
        return importRule;
    }

    #endregion
}
