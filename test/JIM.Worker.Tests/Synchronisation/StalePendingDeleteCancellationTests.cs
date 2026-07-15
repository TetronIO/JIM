// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Core;
using JIM.Models.Logic;
using JIM.Models.Search;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using JIM.Worker.Tests.Workflows;
using Microsoft.EntityFrameworkCore;

namespace JIM.Worker.Tests.Synchronisation;

/// <summary>
/// Regression tests for issue #1018: a Delete Pending Export staged when a Metaverse Object left the
/// scope of an export Synchronisation Rule must be cancelled when the object is evaluated back in
/// scope, even when the returning change touches no exported attribute (the scoping attribute appears
/// in no Attribute Flow Rule of the export rule). Without cancellation the stale Delete would export
/// and delete a live, in-scope object's target account.
/// </summary>
[TestFixture]
public class StalePendingDeleteCancellationTests : WorkflowTestBase
{
    /// <summary>
    /// Everything the scoped-export scenario tests need to drive sync runs and assert on the target CSO.
    /// </summary>
    private sealed record ScopedExportScenario(
        ConnectedSystem SourceSystem,
        ConnectedSystemObject SourceCso,
        ConnectedSystemObjectAttributeValue SourceStatusValue,
        ConnectedSystem TargetSystem,
        ConnectedSystemObjectType TargetType,
        ConnectedSystemObject TargetCso,
        MetaverseObjectType MvType,
        Guid MvoId);

    /// <summary>
    /// The defect scenario (issue #1018): a Metaverse Object leaves the scope of an export
    /// Synchronisation Rule (a Delete Pending Export is staged for its joined target CSO), then
    /// returns to scope via a change that touches ONLY the scoping attribute, which appears in no
    /// Attribute Flow Rule of the export rule. The stale Delete Pending Export must be cancelled;
    /// otherwise the next export run deletes the live target account.
    /// </summary>
    [Test]
    public async Task FullSync_MvoReturnsToScopeWithNoExportedAttributeDelta_CancelsStalePendingDeleteAsync()
    {
        var scenario = await ArrangeScopedExportScenarioAsync();

        // Step 1: scope out. Only the scoping attribute changes; deprovisioning stages a Delete
        // Pending Export for the joined target CSO (sanity check, expected to pass today).
        scenario.SourceStatusValue.StringValue = "Inactive";
        await ModifyCsoAsync(scenario.SourceCso);
        await RunSourceFullSyncAsync(scenario.SourceSystem, "Scope Out Full Sync");

        var deletesAfterScopeOut = GetPendingDeletePendingExportsFor(scenario.TargetCso.Id);
        Assert.That(deletesAfterScopeOut, Has.Count.EqualTo(1),
            "Sanity: leaving export rule scope must stage a Delete Pending Export for the joined target CSO.");

        // Step 2: scope back in. The returning change touches ONLY the scoping attribute, which
        // appears in no Attribute Flow Rule of the export rule, so no attribute changes are staged.
        scenario.SourceStatusValue.StringValue = "Active";
        await ModifyCsoAsync(scenario.SourceCso);
        await RunSourceFullSyncAsync(scenario.SourceSystem, "Scope In Full Sync");

        var staleDeletes = GetPendingDeletePendingExportsFor(scenario.TargetCso.Id);
        Assert.That(staleDeletes, Is.Empty,
            "A Metaverse Object evaluated back in scope must have its stale Delete Pending Export " +
            "cancelled (#1018); otherwise the stale deprovision would delete the live target account.");
    }

    /// <summary>
    /// Same-page exclusion pin (issue #1018): when a Metaverse Object goes OUT of scope for one export
    /// rule during the page while a second export rule on the SAME target system still has it in scope,
    /// the Delete Pending Export staged by the scope-out must survive the page flush. The in-scope
    /// evaluation of the unconditional rule must not cancel a deprovision staged in the same page.
    /// </summary>
    [Test]
    public async Task FullSync_SamePageScopeOutWithSecondInScopeRuleOnSameSystem_KeepsDeletePendingExportAsync()
    {
        var scenario = await ArrangeScopedExportScenarioAsync();

        // A second export rule on the SAME target system with no Object Scoping Criteria: every
        // Metaverse Object of the type is always in scope for it.
        await CreateExportSyncRuleWithoutProvisioningAsync(
            scenario.TargetSystem.Id, scenario.TargetType, scenario.MvType, "Unconditional Target Export");

        scenario.SourceStatusValue.StringValue = "Inactive";
        await ModifyCsoAsync(scenario.SourceCso);
        await RunSourceFullSyncAsync(scenario.SourceSystem, "Scope Out Full Sync");

        var deletes = GetPendingDeletePendingExportsFor(scenario.TargetCso.Id);
        Assert.That(deletes, Has.Count.EqualTo(1),
            "A Delete Pending Export staged by a same-page scope-out must survive the page flush even " +
            "when another export rule for the same system still has the Metaverse Object in scope (#1018).");
    }

    /// <summary>
    /// Recall exclusion pin (issue #1018): reference recall is not a desired-state assertion for
    /// existence, so an evaluation with recall semantics must collect NO in-scope joined CSO ids,
    /// while the same evaluation with desired-state semantics must collect the joined CSO even when
    /// no attribute changes are staged.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_WithRecallSemantics_CollectsNoInScopeJoinedCsoIdsAsync()
    {
        var (mvo, targetCso, cache) = ArrangeDirectEvaluationScenario();

        var recallResult = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, [], sourceSystem: null, cache, recallSemantics: true);

        Assert.That(recallResult.InScopeJoinedCsoIds, Is.Empty,
            "Reference recall must not nominate CSOs for stale Delete Pending Export cancellation (#1018).");

        var desiredStateResult = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, [], sourceSystem: null, cache);

        Assert.That(desiredStateResult.InScopeJoinedCsoIds, Is.EquivalentTo(new[] { targetCso.Id }),
            "A desired-state evaluation must collect the joined, non-PendingProvisioning CSO even when " +
            "no attribute changes are staged.");
    }

    /// <summary>
    /// PendingProvisioning exclusion pin (issue #1018): a CSO that has not yet been exported has no
    /// target account whose existence a desired-state evaluation could assert, so it must not be
    /// collected for stale Delete Pending Export cancellation.
    /// </summary>
    [Test]
    public async Task EvaluateExportRules_WithPendingProvisioningCso_CollectsNoInScopeJoinedCsoIdsAsync()
    {
        var (mvo, targetCso, cache) = ArrangeDirectEvaluationScenario();
        targetCso.Status = ConnectedSystemObjectStatus.PendingProvisioning;

        var result = await Jim.ExportEvaluation.EvaluateExportRulesWithNoNetChangeDetectionAsync(
            mvo, [], sourceSystem: null, cache);

        Assert.That(result.InScopeJoinedCsoIds, Is.Empty,
            "A PendingProvisioning CSO has no target account to assert existence for and must not be " +
            "collected for stale Delete Pending Export cancellation (#1018).");
    }

    #region helpers

    /// <summary>
    /// Builds the minimal state for calling the export evaluation server directly: a Metaverse Object,
    /// an export Synchronisation Rule with no Object Scoping Criteria (always in scope) and no
    /// Attribute Flow Rules, a joined Normal-status target CSO, and a hand-built evaluation cache.
    /// </summary>
    private static (MetaverseObject Mvo, ConnectedSystemObject TargetCso, ExportEvaluationCache Cache) ArrangeDirectEvaluationScenario()
    {
        const int targetSystemId = 903;

        var mvType = new MetaverseObjectType { Id = 901, Name = "RecallPinUser" };
        var mvo = new MetaverseObject { Id = Guid.NewGuid(), Type = mvType };

        var exportRule = new SyncRule
        {
            Id = 902,
            ConnectedSystemId = targetSystemId,
            Name = "Recall Pin Export Rule",
            Direction = SyncRuleDirection.Export,
            Enabled = true,
            MetaverseObjectTypeId = mvType.Id,
            MetaverseObjectType = mvType
        };

        var targetCso = new ConnectedSystemObject
        {
            Id = Guid.NewGuid(),
            ConnectedSystemId = targetSystemId,
            Status = ConnectedSystemObjectStatus.Normal,
            MetaverseObjectId = mvo.Id,
            MetaverseObject = mvo,
            JoinType = ConnectedSystemObjectJoinType.Provisioned
        };

        var exportRulesByMvoTypeId = new Dictionary<int, List<SyncRule>> { { mvType.Id, new List<SyncRule> { exportRule } } };
        var csoLookup = new Dictionary<(Guid MvoId, int ConnectedSystemId), ConnectedSystemObject> { { (mvo.Id, targetSystemId), targetCso } };
        var csoAttributeValues = Enumerable.Empty<ConnectedSystemObjectAttributeValue>()
            .ToLookup(av => (av.ConnectedSystemObject.Id, av.AttributeId));
        var cache = new ExportEvaluationCache(exportRulesByMvoTypeId, csoLookup, csoAttributeValues, new List<int> { targetSystemId });

        return (mvo, targetCso, cache);
    }

    /// <summary>
    /// Builds the scoped-export scenario: a source system feeding a Metaverse Object whose "Status"
    /// attribute drives an export rule's Object Scoping Criteria (Status == "Active"), a target system
    /// with that export rule (Status appears in NO Attribute Flow Rule, deprovision action Delete),
    /// and a Normal-status target CSO joined to the Metaverse Object.
    /// </summary>
    private async Task<ScopedExportScenario> ArrangeScopedExportScenarioAsync()
    {
        var sourceSystem = await CreateConnectedSystemAsync("Scope Source System");
        var targetSystem = await CreateConnectedSystemAsync("Scope Target System");

        var sourceType = await CreateCsoTypeAsync(sourceSystem.Id, "User", new List<ConnectedSystemObjectTypeAttribute>
        {
            new() { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true },
            new() { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true },
            new() { Name = "Status", Type = AttributeDataType.Text, Selected = true }
        });
        var targetType = await CreateCsoTypeAsync(targetSystem.Id, "User");

        var mvType = await CreateMvObjectTypeAsync("Person");
        var statusMvAttr = await AddMetaverseAttributeAsync(mvType, "Status");

        // Import Synchronisation Rule: project to the Metaverse and flow source Status -> Metaverse Status.
        var importRule = await CreateImportSyncRuleAsync(sourceSystem.Id, sourceType, mvType, "Scope Source Import");
        var sourceStatusAttr = sourceType.Attributes.Single(a => a.Name == "Status");
        importRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = importRule,
            TargetMetaverseAttribute = statusMvAttr,
            TargetMetaverseAttributeId = statusMvAttr.Id,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Order = 0,
                    ConnectedSystemAttribute = sourceStatusAttr,
                    ConnectedSystemAttributeId = sourceStatusAttr.Id
                }
            }
        });

        // Export Synchronisation Rule: scoped to Status == "Active", deprovision action Delete.
        // The scoping attribute (Status) intentionally appears in NO Attribute Flow Rule; only
        // DisplayName is flowed, so a Status-only change stages no attribute changes on export.
        var exportRule = await CreateExportSyncRuleWithoutProvisioningAsync(
            targetSystem.Id, targetType, mvType, "Scope Target Export");
        exportRule.OutboundDeprovisionAction = OutboundDeprovisionAction.Delete;

        // First, not Single: EF relationship fixup can add the attribute to the type's collection a
        // second time when the base helper also adds it manually (both entries are the same instance).
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var targetDisplayNameAttr = targetType.Attributes.Single(a => a.Name == "DisplayName");
        exportRule.AttributeFlowRules.Add(new SyncRuleMapping
        {
            SyncRule = exportRule,
            TargetConnectedSystemAttribute = targetDisplayNameAttr,
            TargetConnectedSystemAttributeId = targetDisplayNameAttr.Id,
            Sources =
            {
                new SyncRuleMappingSource
                {
                    Order = 0,
                    MetaverseAttribute = mvDisplayNameAttr,
                    MetaverseAttributeId = mvDisplayNameAttr.Id
                }
            }
        });

        exportRule.ObjectScopingCriteriaGroups.Add(new SyncRuleScopingCriteriaGroup
        {
            Type = SearchGroupType.All,
            Criteria = new List<SyncRuleScopingCriteria>
            {
                new()
                {
                    MetaverseAttribute = statusMvAttr,
                    ComparisonType = SearchComparisonType.Equals,
                    StringValue = "Active",
                    CaseSensitive = true
                }
            }
        });

        // Source CSO starts in scope (Status == "Active").
        var sourceCso = await CreateCsoAsync(sourceSystem.Id, sourceType, "Kai Turner", "EMP1018");
        var sourceStatusValue = new ConnectedSystemObjectAttributeValue
        {
            Id = Guid.NewGuid(),
            AttributeId = sourceStatusAttr.Id,
            Attribute = sourceStatusAttr,
            StringValue = "Active"
        };
        sourceCso.AttributeValues.Add(sourceStatusValue);

        // Bootstrap run: project the Metaverse Object with Status flowed in.
        await RunSourceFullSyncAsync(sourceSystem, "Bootstrap Full Sync");

        sourceCso = await ReloadEntityAsync(sourceCso);
        Assert.That(sourceCso.MetaverseObjectId, Is.Not.Null,
            "Arrange: source CSO should be joined to a projected Metaverse Object after the bootstrap Full Sync.");
        var mvoId = sourceCso.MetaverseObjectId!.Value;

        // Target CSO joined to the Metaverse Object with Normal status (a live, exported account).
        var targetCso = await CreateCsoAsync(targetSystem.Id, targetType, "Kai Turner", "EMP1018");
        targetCso.MetaverseObjectId = mvoId;
        targetCso.JoinType = ConnectedSystemObjectJoinType.Provisioned;
        targetCso.Status = ConnectedSystemObjectStatus.Normal;
        targetCso.DateJoined = DateTime.UtcNow;
        SyncRepo.RefreshCsoMvoIndex(targetCso);

        return new ScopedExportScenario(
            sourceSystem, sourceCso, sourceStatusValue, targetSystem, targetType, targetCso, mvType, mvoId);
    }

    /// <summary>
    /// Runs a Full Synchronisation on the given system with a fresh Run Profile and Activity.
    /// </summary>
    private async Task RunSourceFullSyncAsync(ConnectedSystem system, string runProfileName)
    {
        var runProfile = await CreateRunProfileAsync(system.Id, runProfileName, ConnectedSystemRunType.FullSynchronisation);
        system = await ReloadEntityAsync(system);
        var activity = await CreateActivityAsync(system.Id, runProfile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(
                new SyncEngine(), new SyncServer(Jim), SyncRepo, system, runProfile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
    }

    /// <summary>
    /// Returns the Pending-status Delete Pending Exports currently held for the given CSO.
    /// </summary>
    private List<PendingExport> GetPendingDeletePendingExportsFor(Guid connectedSystemObjectId)
    {
        return SyncRepo.PendingExports.Values
            .Where(pe => pe.ConnectedSystemObjectId == connectedSystemObjectId &&
                         pe.ChangeType == PendingExportChangeType.Delete &&
                         pe.Status == PendingExportStatus.Pending)
            .ToList();
    }

    /// <summary>
    /// Creates an export Synchronisation Rule with provisioning disabled, so target CSOs are only
    /// ever the ones the tests join by hand.
    /// </summary>
    private async Task<SyncRule> CreateExportSyncRuleWithoutProvisioningAsync(
        int connectedSystemId,
        ConnectedSystemObjectType csoType,
        MetaverseObjectType mvType,
        string name)
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
            ProvisionToConnectedSystem = false
        };

        // Detach modified entities to avoid EF trying to persist processor-modified properties
        // when this helper runs after a sync (matching the base helpers).
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList())
            entry.State = EntityState.Detached;
        DbContext.SyncRules.Add(syncRule);
        await DbContext.SaveChangesAsync();

        SyncRepo.SeedSyncRule(syncRule);

        return syncRule;
    }

    /// <summary>
    /// Adds a single-valued text Metaverse Attribute to the given Metaverse Object Type.
    /// </summary>
    private async Task<MetaverseAttribute> AddMetaverseAttributeAsync(MetaverseObjectType mvType, string name)
    {
        var attribute = new MetaverseAttribute
        {
            Name = name,
            Type = AttributeDataType.Text,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = new List<MetaverseObjectType> { mvType },
            PredefinedSearchAttributes = new List<JIM.Models.Search.PredefinedSearchAttribute>()
        };

        // Detach modified entities so EF only persists the new attribute (matching the base helpers).
        foreach (var entry in DbContext.ChangeTracker.Entries().Where(e => e.State == EntityState.Modified).ToList())
            entry.State = EntityState.Detached;
        DbContext.MetaverseAttributes.Add(attribute);
        await DbContext.SaveChangesAsync();

        // EF relationship fixup may already have added the attribute via the MetaverseObjectTypes
        // navigation; only add it manually when it has not.
        if (!mvType.Attributes.Contains(attribute))
            mvType.Attributes.Add(attribute);

        return attribute;
    }

    #endregion
}
