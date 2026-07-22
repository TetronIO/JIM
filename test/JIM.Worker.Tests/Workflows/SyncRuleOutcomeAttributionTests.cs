// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Tests for GitHub issue #1085: persist the scoping Synchronisation Rule on Out of Scope sync outcomes.
///
/// When a CSO falls out of scope during synchronisation, the DisconnectedOutOfScope causality outcome
/// must record which Synchronisation Rule the object fell out of scope of (rule id plus a name snapshot,
/// so the attribution survives later rule renames or deletions). Opportunistically, Projected outcomes
/// carry the projecting Synchronisation Rule too, since it is already in hand at decision time.
/// </summary>
[TestFixture]
public class SyncRuleOutcomeAttributionTests : WorkflowTestBase
{
    #region Out of Scope Attribution

    /// <summary>
    /// Verifies that when a CSO falls out of scope of an import Synchronisation Rule's scoping criteria,
    /// the DisconnectedOutOfScope root outcome records the scoping Synchronisation Rule's id and name snapshot.
    /// </summary>
    [Test]
    public async Task HandleCsoOutOfScope_CsoFallsOutOfScope_RootOutcomeCarriesScopingSyncRuleAsync()
    {
        // Arrange: source system with an import Synchronisation Rule that has scoping criteria
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");
        sourceType.RemoveContributedAttributesOnObsoletion = true;

        var mvType = await CreateMvObjectTypeAsync("Person");

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

        // Scoping criteria: EmployeeId must equal "EMP001"
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

        // Create a CSO in scope and run a Full Sync so it projects and joins
        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");

        // Change the CSO's EmployeeId so it falls out of scope
        var empIdAttrValue = cso.AttributeValues.Single(av => av.Attribute?.Name == "EmployeeId");
        empIdAttrValue.StringValue = "OUT_OF_SCOPE";
        cso.LastUpdated = DateTime.UtcNow;

        // Act: run a second Full Sync; the CSO is now out of scope
        var fullSync2Profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync 2", ConnectedSystemRunType.FullSynchronisation);
        sourceSystem = await ReloadEntityAsync(sourceSystem);
        var fullSync2Activity = await CreateActivityAsync(sourceSystem.Id, fullSync2Profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSync2Profile, fullSync2Activity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Assert: the DisconnectedOutOfScope root outcome carries the scoping Synchronisation Rule
        var outOfScopeRpei = fullSync2Activity.RunProfileExecutionItems
            .FirstOrDefault(r => r.ObjectChangeType == ObjectChangeType.DisconnectedOutOfScope);
        Assert.That(outOfScopeRpei, Is.Not.Null, "Should have a DisconnectedOutOfScope RPEI");

        var rootOutcome = outOfScopeRpei!.SyncOutcomes
            .SingleOrDefault(o => o.ParentSyncOutcome == null);
        Assert.That(rootOutcome, Is.Not.Null, "RPEI should have a root outcome");
        var rootOutcomeNotNull = rootOutcome!;
        Assert.That(rootOutcomeNotNull.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope));
        Assert.That(rootOutcomeNotNull.SyncRuleId, Is.EqualTo(importRule.Id),
            "The DisconnectedOutOfScope outcome should record the id of the scoping Synchronisation Rule");
        Assert.That(rootOutcomeNotNull.SyncRuleName, Is.EqualTo(importRule.Name),
            "The DisconnectedOutOfScope outcome should record a name snapshot of the scoping Synchronisation Rule");
    }

    #endregion

    #region Projected Attribution (opportunistic)

    /// <summary>
    /// Verifies that when a CSO is projected with Attribute Flow (the RPEI created during
    /// ProcessMetaverseObjectChangesAsync), the Projected root outcome records the projecting
    /// Synchronisation Rule's id and name snapshot.
    /// </summary>
    [Test]
    public async Task ProcessMetaverseObjectChanges_CsoProjectedWithAttributeFlow_ProjectedOutcomeCarriesSyncRuleAsync()
    {
        // Arrange: import Synchronisation Rule with projection enabled and one Attribute Flow mapping
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");

        var mvType = await CreateMvObjectTypeAsync("Person");

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

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        // Act: Full Sync projects the CSO to the Metaverse
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");

        // Assert: the Projected root outcome carries the projecting Synchronisation Rule
        var projectedRpei = fullSyncActivity.RunProfileExecutionItems
            .FirstOrDefault(r => r.SyncOutcomes.Any(o =>
                o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
        Assert.That(projectedRpei, Is.Not.Null, "Should have an RPEI with a Projected outcome");

        var projectedOutcome = projectedRpei!.SyncOutcomes
            .Single(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        Assert.That(projectedOutcome.SyncRuleId, Is.EqualTo(importRule.Id),
            "The Projected outcome should record the id of the projecting Synchronisation Rule");
        Assert.That(projectedOutcome.SyncRuleName, Is.EqualTo(importRule.Name),
            "The Projected outcome should record a name snapshot of the projecting Synchronisation Rule");
    }

    /// <summary>
    /// Verifies that when a CSO is projected without any Attribute Flow (the RPEI created during
    /// ProcessActiveConnectedSystemObjectAsync from the change result), the Projected root outcome
    /// still records the projecting Synchronisation Rule.
    /// </summary>
    [Test]
    public async Task ProcessActiveConnectedSystemObject_CsoProjectedWithoutAttributeFlow_ProjectedOutcomeCarriesSyncRuleAsync()
    {
        // Arrange: import Synchronisation Rule with projection enabled but NO Attribute Flow mappings
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");

        var mvType = await CreateMvObjectTypeAsync("Person");

        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "HR Import");

        var cso = await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        // Act: Full Sync projects the CSO to the Metaverse (no attributes flow)
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        cso = await ReloadEntityAsync(cso);
        Assert.That(cso.MetaverseObjectId, Is.Not.Null, "CSO should be joined to MVO after Full Sync");

        // Assert: the Projected root outcome carries the projecting Synchronisation Rule
        var projectedRpei = fullSyncActivity.RunProfileExecutionItems
            .FirstOrDefault(r => r.SyncOutcomes.Any(o =>
                o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
        Assert.That(projectedRpei, Is.Not.Null, "Should have an RPEI with a Projected outcome");

        var projectedOutcome = projectedRpei!.SyncOutcomes
            .Single(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        Assert.That(projectedOutcome.SyncRuleId, Is.EqualTo(importRule.Id),
            "The Projected outcome should record the id of the projecting Synchronisation Rule");
        Assert.That(projectedOutcome.SyncRuleName, Is.EqualTo(importRule.Name),
            "The Projected outcome should record a name snapshot of the projecting Synchronisation Rule");
    }

    /// <summary>
    /// Verifies the null case remains valid: a Joined outcome (where no Synchronisation Rule is
    /// attributed by this change) carries null Synchronisation Rule attribution fields without error.
    /// </summary>
    [Test]
    public async Task ProcessMetaverseObjectChanges_CsoJoinedWithoutRuleAttribution_SyncRuleFieldsRemainNullAsync()
    {
        // Arrange: same shape as the projection test; the second CSO projection produces a Projected
        // outcome, but the AttributeFlow child outcome has no rule attribution and must remain null.
        var sourceSystem = await CreateConnectedSystemAsync("HR Source");
        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User");

        var mvType = await CreateMvObjectTypeAsync("Person");

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

        await CreateCsoAsync(sourceSystem.Id, sourceType, "John Smith", "EMP001");

        // Act
        var fullSyncProfile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var fullSyncActivity = await CreateActivityAsync(sourceSystem.Id, fullSyncProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, fullSyncProfile, fullSyncActivity, new CancellationTokenSource())
            .PerformFullSyncAsync();

        // Assert: the AttributeFlow child under the Projected root has no rule attribution
        var projectedRpei = fullSyncActivity.RunProfileExecutionItems
            .First(r => r.SyncOutcomes.Any(o =>
                o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
        var attributeFlowOutcome = projectedRpei.SyncOutcomes
            .SingleOrDefault(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        Assert.That(attributeFlowOutcome, Is.Not.Null, "Should have an AttributeFlow child outcome in Detailed mode");
        Assert.That(attributeFlowOutcome!.SyncRuleId, Is.Null,
            "Outcomes without a determinable Synchronisation Rule must leave SyncRuleId null");
        Assert.That(attributeFlowOutcome!.SyncRuleName, Is.Null,
            "Outcomes without a determinable Synchronisation Rule must leave SyncRuleName null");
    }

    #endregion
}
