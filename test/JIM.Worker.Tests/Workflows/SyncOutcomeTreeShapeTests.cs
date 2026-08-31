// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using JIM.Application.Servers;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Logic;
using JIM.Models.Staging;
using JIM.Models.Transactional;
using JIM.Worker.Processors;
using NUnit.Framework;

namespace JIM.Worker.Tests.Workflows;

/// <summary>
/// Pins the shape and content of the causality tree a real synchronisation records (#1428).
///
/// The design (#363) nests Provisioned and Pending Export Created under the Attribute Flow child,
/// because the Metaverse Object is only fully formed once Attribute Flow has run: what is exported is
/// caused by what flowed in, not merely by the same object having been projected. Every test here also
/// exercises the pre-persistence parent lookup that made the builder disagree with that design: while a
/// tree is being built the parent link is the navigation property and the FK is still null, so a lookup
/// keyed on the FK alone finds no children at all.
///
/// These are absolute assertions on the recorded tree, deliberately not a preview-versus-real pairing:
/// <see cref="SyncPreviewFidelityTests"/> compares the two trees to each other, so it holds whatever
/// shape both sides agree on, and both sides agreed on the wrong one until this was fixed.
/// </summary>
[TestFixture]
public class SyncOutcomeTreeShapeTests : WorkflowTestBase
{
    /// <summary>
    /// A projecting import that flows a display name, and an export rule that provisions a target object
    /// and stages its Pending Export. The recorded chain must be
    /// Projected -> Attribute Flow -> Provisioned -> Pending Export Created.
    /// </summary>
    [Test]
    public async Task PerformFullSyncAsync_ProjectionProvisionsAnExport_ExportOutcomesNestUnderTheAttributeFlowChildAsync()
    {
        // Arrange
        var (sourceSystem, _) = await ArrangeProjectingAndProvisioningTopologyAsync();

        // Act
        var activity = await RunFullSyncAsync(sourceSystem);

        // Assert - the whole chain hangs off the Attribute Flow child, not off the root
        var root = SingleRootOutcome(activity);
        Assert.That(root.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Projected));

        var attributeFlowChild = root.Children.SingleOrDefault(o =>
            o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        Assert.That(attributeFlowChild, Is.Not.Null,
            "The projection must record an Attribute Flow child to hang the export outcomes from");

        var provisioned = attributeFlowChild!.Children.SingleOrDefault(o =>
            o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(provisioned, Is.Not.Null,
                "Provisioned must be recorded as a child of the Attribute Flow outcome, not of the root");
            Assert.That(root.Children.Select(c => c.OutcomeType), Has.Exactly(1).Items,
                "The root's only child is the Attribute Flow outcome; export outcomes are its descendants");
            Assert.That(provisioned!.Children.Select(c => c.OutcomeType),
                Does.Contain(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated),
                "The Pending Export staged for the provisioned object nests under its Provisioned outcome");
        }

        var pendingExportOutcome = provisioned!.Children
            .Single(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);
        Assert.That(pendingExportOutcome.StagedChangeType, Is.EqualTo(PendingExportChangeType.Create),
            "A newly provisioned object's Pending Export is a Create, and the outcome must record that staged kind");
    }

    /// <summary>
    /// The same chain re-run over a changed source value, once the target object already exists: the
    /// staged Pending Export is now an Update, not a Create, and the outcome must record which (#1561
    /// follow-up: the Export queued chip needs the staged kind, not just the outcome type).
    /// </summary>
    [Test]
    public async Task PerformFullSyncAsync_ExistingProvisionedObjectAttributeChanges_PendingExportOutcomeRecordsUpdateAsync()
    {
        // Arrange - first sync projects, provisions and stages the Create
        var (sourceSystem, cso) = await ArrangeProjectingAndProvisioningTopologyAsync();
        await RunFullSyncAsync(sourceSystem);
        var reloadedCso = await ReloadEntityAsync(cso);

        // Simulate the first Create having already been exported and confirmed: the target CSO leaves
        // PendingProvisioning for Normal, and its Create Pending Export is cleared from the queue. Without
        // both steps the second sync's evaluation still sees a PendingProvisioning CSO and reuses (rather
        // than replaces) the same Create export, per ExportEvaluationServer's ReusePendingProvisioningCso path.
        var provisionedTargetCso = SyncRepo.ConnectedSystemObjects.Values
            .Single(c => c.MetaverseObjectId == reloadedCso.MetaverseObjectId && c.ConnectedSystemId != sourceSystem.Id);
        provisionedTargetCso.Status = ConnectedSystemObjectStatus.Normal;
        SyncRepo.ClearAllPendingExports();

        // The source value changes, so the second sync flows an update and stages an Update Pending Export
        var displayNameAttrValue = reloadedCso.AttributeValues.Single(av => av.Attribute?.Name == "DisplayName");
        displayNameAttrValue.StringValue = "John Smith Jr";
        reloadedCso.LastUpdated = DateTime.UtcNow;

        // Act
        var activity = await RunFullSyncAsync(sourceSystem);

        // Assert - the Pending Export outcome staged by the second sync records an Update
        var pendingExportOutcome = activity.RunProfileExecutionItems
            .SelectMany(r => r.SyncOutcomes)
            .Single(o => o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);
        Assert.That(pendingExportOutcome.StagedChangeType, Is.EqualTo(PendingExportChangeType.Update),
            "An update to an already-provisioned object's attribute stages an Update Pending Export, and the outcome must record that staged kind");
    }

    /// <summary>
    /// The same rule expressed over the flattened collection, so a future change that rebuilds the tree
    /// differently still cannot reattach an export outcome to the root: every export outcome recorded for
    /// a projection or join must sit below the Attribute Flow child.
    /// </summary>
    [Test]
    public async Task PerformFullSyncAsync_ProjectionProvisionsAnExport_NoExportOutcomeAttachesToTheRootAsync()
    {
        // Arrange
        var (sourceSystem, _) = await ArrangeProjectingAndProvisioningTopologyAsync();

        // Act
        var activity = await RunFullSyncAsync(sourceSystem);

        // Assert
        var root = SingleRootOutcome(activity);
        var exportOutcomeTypes = new[]
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned,
            ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued
        };

        Assert.That(root.Children.Where(c => exportOutcomeTypes.Contains(c.OutcomeType)), Is.Empty,
            "Export outcomes must never be recorded as siblings of the Attribute Flow outcome");
    }

    /// <summary>
    /// A reference attribute resolved at the end of the page merges into the Attribute Flow child's count,
    /// rather than leaving it showing only the scalar attributes counted during the main pass. The merge
    /// looks the child up on an outcome tree it built moments earlier, so it hit the same pre-persistence
    /// parent-lookup fault as the export nesting above (#1428) and silently found nothing to update.
    /// </summary>
    [Test]
    public async Task PerformFullSyncAsync_ReferenceResolvedAtPageEnd_AttributeFlowChildCountIncludesTheReferenceAsync()
    {
        // Arrange - one system whose import rule projects and flows a Manager reference between two
        // objects in the same page, so the reference resolves after the main pass
        var (hrSystem, johnCso) = await ArrangeIntraPageReferenceTopologyAsync();

        // Act
        var activity = await RunFullSyncAsync(hrSystem);

        // Assert - the Attribute Flow child agrees with the RPEI's own total, references included
        var johnRpei = activity.RunProfileExecutionItems
            .SingleOrDefault(r => r.ConnectedSystemObjectId == johnCso.Id);
        Assert.That(johnRpei, Is.Not.Null, "The referencing object must have recorded an execution item");

        var attributeFlowChild = johnRpei!.SyncOutcomes.SingleOrDefault(o =>
            o.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow && o.IsChildOutcome);
        Assert.That(attributeFlowChild, Is.Not.Null, "The projection must have recorded an Attribute Flow child");

        using (Assert.EnterMultipleScope())
        {
            Assert.That(attributeFlowChild!.DetailCount, Is.EqualTo(johnRpei.AttributeFlowCount),
                "The Attribute Flow child's count must match the execution item's total once the reference merged");
            Assert.That(attributeFlowChild.DetailCount, Is.GreaterThan(1),
                "The count must include the Manager reference alongside the scalar attributes");
        }
    }

    #region helpers

    /// <summary>
    /// A source system whose import rule projects and flows Display Name, and a target system whose export
    /// rule provisions and flows the same attribute back out, with one unjoined source object waiting.
    /// </summary>
    private async Task<(ConnectedSystem SourceSystem, ConnectedSystemObject Cso)>
        ArrangeProjectingAndProvisioningTopologyAsync()
    {
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
        return (sourceSystem, cso);
    }

    /// <summary>
    /// One HR system whose import rule projects and flows Display Name plus a Manager reference. Mary and
    /// John are both unjoined in the same page, and John's Manager points at Mary, so the reference can
    /// only resolve once both have Metaverse Objects: at the end of the page, through the merge path.
    /// Returns the system and John, the referencing object.
    /// </summary>
    private async Task<(ConnectedSystem HrSystem, ConnectedSystemObject JohnCso)>
        ArrangeIntraPageReferenceTopologyAsync()
    {
        var hrSystem = await CreateConnectedSystemAsync("HR Source");
        var externalIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "ExternalId", Type = AttributeDataType.Guid, IsExternalId = true, Selected = true };
        var displayNameAttr = new ConnectedSystemObjectTypeAttribute { Name = "DisplayName", Type = AttributeDataType.Text, Selected = true };
        var employeeIdAttr = new ConnectedSystemObjectTypeAttribute { Name = "EmployeeId", Type = AttributeDataType.Text, Selected = true };
        var managerAttr = new ConnectedSystemObjectTypeAttribute { Name = "Manager", Type = AttributeDataType.Reference, Selected = true };
        var hrType = await CreateCsoTypeAsync(hrSystem.Id, "HrUser",
            [externalIdAttr, displayNameAttr, employeeIdAttr, managerAttr]);

        var mvType = await CreateMvObjectTypeAsync("Person");
        var mvDisplayNameAttr = mvType.Attributes.First(a => a.Name == "DisplayName");
        var mvEmployeeIdAttr = mvType.Attributes.First(a => a.Name == "EmployeeId");
        var mvManagerAttr = new MetaverseAttribute
        {
            Name = "Manager",
            Type = AttributeDataType.Reference,
            AttributePlurality = AttributePlurality.SingleValued,
            MetaverseObjectTypes = [mvType],
            PredefinedSearchAttributes = []
        };
        DbContext.MetaverseAttributes.Add(mvManagerAttr);
        await DbContext.SaveChangesAsync();
        mvType.Attributes.Add(mvManagerAttr);

        var importRule = await CreateImportSyncRuleAsync(hrSystem.Id, hrType, mvType, "HR Import");
        importRule.AttributeFlowRules.Add(BuildDirectImportMapping(importRule, mvDisplayNameAttr, displayNameAttr));
        importRule.AttributeFlowRules.Add(BuildDirectImportMapping(importRule, mvEmployeeIdAttr, employeeIdAttr));
        importRule.AttributeFlowRules.Add(BuildDirectImportMapping(importRule, mvManagerAttr, managerAttr));
        await DbContext.SaveChangesAsync();

        var maryCso = await CreateCsoAsync(hrSystem.Id, hrType, "Mary Manager", "EMP002");
        var johnCso = await CreateCsoAsync(hrSystem.Id, hrType, "John Smith", "EMP001");
        johnCso.AttributeValues.Add(new ConnectedSystemObjectAttributeValue
        {
            AttributeId = managerAttr.Id,
            Attribute = managerAttr,
            ReferenceValueId = maryCso.Id,
            ReferenceValue = maryCso,
            ConnectedSystemObject = johnCso
        });
        await DbContext.SaveChangesAsync();

        return (hrSystem, johnCso);
    }

    /// <summary>
    /// Builds a single-source import Attribute Flow mapping from a Connected System attribute.
    /// </summary>
    private static SyncRuleMapping BuildDirectImportMapping(
        SyncRule rule,
        MetaverseAttribute target,
        ConnectedSystemObjectTypeAttribute source)
    {
        return new SyncRuleMapping
        {
            SyncRule = rule,
            SyncRuleId = rule.Id,
            TargetMetaverseAttribute = target,
            TargetMetaverseAttributeId = target.Id,
            Sources = { new SyncRuleMappingSource { Order = 0, ConnectedSystemAttribute = source, ConnectedSystemAttributeId = source.Id } }
        };
    }

    /// <summary>
    /// Runs a real Full Synchronisation over the source system and returns its Activity.
    /// </summary>
    private async Task<Activity> RunFullSyncAsync(ConnectedSystem sourceSystem)
    {
        var profile = await CreateRunProfileAsync(sourceSystem.Id, "Full Sync", ConnectedSystemRunType.FullSynchronisation);
        var activity = await CreateActivityAsync(sourceSystem.Id, profile, ConnectedSystemRunType.FullSynchronisation);
        await new SyncFullSyncTaskProcessor(new SyncEngine(), new SyncServer(Jim), SyncRepo, sourceSystem, profile, activity, new CancellationTokenSource())
            .PerformFullSyncAsync();
        return activity;
    }

    /// <summary>
    /// The run's single root outcome. Roots are identified by both links, because the parent FK is only
    /// resolved at bulk-insert flattening while the navigation is set as the tree is built.
    /// </summary>
    private static ActivityRunProfileExecutionItemSyncOutcome SingleRootOutcome(Activity activity)
    {
        var roots = activity.RunProfileExecutionItems
            .SelectMany(rpei => rpei.SyncOutcomes)
            .Where(o => o.ParentSyncOutcome == null && !o.ParentSyncOutcomeId.HasValue)
            .ToList();

        Assert.That(roots, Has.Count.EqualTo(1), "The run must have recorded exactly one root outcome");
        return roots[0];
    }

    #endregion
}
