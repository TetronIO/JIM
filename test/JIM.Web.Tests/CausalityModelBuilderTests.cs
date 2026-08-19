// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="CausalityModelBuilder"/>: transforming a Run Profile Execution Item and its
/// page context into the causality event tree consumed by the redesigned visualisation.
/// </summary>
[TestFixture]
public class CausalityModelBuilderTests
{
    [Test]
    public void Build_NewJoinerScenario_ProducesExpectedTreeShape()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        Assert.That(model.Roots, Has.Count.EqualTo(1));
        var projected = model.Roots[0];
        Assert.That(projected.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Projected));
        Assert.That(projected.Children, Has.Count.EqualTo(1));

        var attributeFlow = projected.Children[0];
        Assert.That(attributeFlow.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow));
        Assert.That(attributeFlow.DetailCount, Is.EqualTo(11));

        var provisioned = attributeFlow.Children[0];
        Assert.That(provisioned.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned));

        var pendingExport = provisioned.Children[0];
        Assert.That(pendingExport.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated));
    }

    [Test]
    public void Build_NewJoinerScenario_AssignsLanes()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var projected = model.Roots[0];
        var attributeFlow = projected.Children[0];
        var provisioned = attributeFlow.Children[0];
        var pendingExport = provisioned.Children[0];

        Assert.That(projected.Lane, Is.EqualTo(CausalityLane.Identity));
        Assert.That(attributeFlow.Lane, Is.EqualTo(CausalityLane.Identity));
        Assert.That(provisioned.Lane, Is.EqualTo(CausalityLane.Downstream));
        Assert.That(pendingExport.Lane, Is.EqualTo(CausalityLane.Downstream));
    }

    [Test]
    public void Build_EveryOutcomeType_AssignsExpectedLane()
    {
        var expectedLanes = new Dictionary<ActivityRunProfileExecutionItemSyncOutcomeType, CausalityLane>
        {
            [ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded] = CausalityLane.Source,
            [ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated] = CausalityLane.Source,
            [ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted] = CausalityLane.Source,
            [ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected] = CausalityLane.Source,
            [ActivityRunProfileExecutionItemSyncOutcomeType.ExportConfirmed] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Projected] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Joined] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.DriftCorrection] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Exported] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor] = CausalityLane.Identity,

            // Configuration change preview types. Nothing writes these during a run, so they never reach a lane
            // in practice; they land in Identity via the default arm, which is the correct home for them anyway
            // (a preview delta describes the Metaverse-side consequence of a proposal, not an import or export).
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallInScope] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldBecomeDeletionEligible] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldCeaseToBeDeletionEligible] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeletionEligibleDate] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldDisconnectFromMetaverseObject] = CausalityLane.Identity,
            // WouldStageDeleteExport describes the same export-side event as DeprovisionQueued, so it shares
            // its Downstream lane; the other two destructive-toggle preview transitions are Metaverse-side.
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStageDeleteExport] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldRemainJoined] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction] = CausalityLane.Identity
        };

        Assert.That(expectedLanes.Keys, Is.EquivalentTo(Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>()),
            "The expected lane table must cover every outcome type");

        foreach (var (outcomeType, expectedLane) in expectedLanes)
        {
            var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
            CausalityTestData.AddOutcome(item, outcomeType, parent: null, ordinal: 0);

            var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

            Assert.That(model.Roots[0].Lane, Is.EqualTo(expectedLane), $"Lane mismatch for {outcomeType}");
        }
    }

    [Test]
    public void Build_ProjectedWithSyncRule_LinksIdentityAndSynchronisationRule()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var projected = model.Roots[0];

        var identityLink = projected.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.Identity);
        Assert.That(identityLink, Is.Not.Null);
        Assert.That(identityLink!.Label, Is.EqualTo("Liam Allen"));
        Assert.That(identityLink.Href, Is.EqualTo($"/t/people/v/{CausalityTestData.MvoId}"));

        var ruleLink = projected.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.SynchronisationRule);
        Assert.That(ruleLink, Is.Not.Null);
        Assert.That(ruleLink!.Label, Is.EqualTo("Yellowstone People - Inbound"));
        Assert.That(ruleLink.Href, Is.EqualTo("/admin/sync-rules/5"));
    }

    [Test]
    public void Build_ProvisionedOutcome_LinksConnectedSystemAndRecordFromDetailMessage()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var provisioned = model.Roots[0].Children[0].Children[0];

        Assert.That(provisioned.SystemId, Is.EqualTo(2));
        Assert.That(provisioned.SystemName, Is.EqualTo("Glitterband EMEA"));

        var csLink = provisioned.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.ConnectedSystem);
        Assert.That(csLink, Is.Not.Null);
        Assert.That(csLink!.Label, Is.EqualTo("Glitterband EMEA"));
        Assert.That(csLink.Href, Is.EqualTo("/admin/connected-systems/2"));

        var recordLink = provisioned.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.Record);
        Assert.That(recordLink, Is.Not.Null);
        Assert.That(recordLink!.Href, Is.EqualTo($"/admin/connected-systems/2/connector-space/{CausalityTestData.ProvisionedCsoId}"));
        Assert.That(recordLink.Label, Does.Contain("person"));

        // The Provisioned target entity is a CSO, never an Identity
        Assert.That(provisioned.Links.Any(l => l.Kind == CausalityEntityKind.Identity), Is.False);
    }

    [Test]
    public void Build_PendingExportCreatedOutcome_LinksConnectedSystemAndPendingExports()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var pendingExport = model.Roots[0].Children[0].Children[0].Children[0];

        Assert.That(pendingExport.SystemId, Is.EqualTo(2));

        var csLink = pendingExport.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.ConnectedSystem);
        Assert.That(csLink, Is.Not.Null);
        Assert.That(csLink!.Href, Is.EqualTo("/admin/connected-systems/2"));

        // TargetEntityId is the Pending Export's own id, so the link lands on that Pending Export
        // rather than on the target system's whole queue for the reader to hunt through
        var peLink = pendingExport.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.PendingExport);
        Assert.That(peLink, Is.Not.Null);
        Assert.That(peLink!.Href, Is.EqualTo($"/admin/connected-systems/2/pending-exports/{CausalityTestData.PendingExportId}"));

        // The PendingExportCreated target entity is a Pending Export id, never an Identity
        Assert.That(pendingExport.Links.Any(l => l.Kind == CausalityEntityKind.Identity), Is.False);
    }

    [Test]
    public void Build_PendingExportAlreadyExported_LinksTheQueueRatherThanTheDeletedRow()
    {
        // A Pending Export is hard-deleted once it has been exported, so on any item older than the
        // next export run the individual row is gone and a link to it 404s. The causality record is
        // permanent, so the link has to degrade to the queue rather than promise a row that no longer
        // exists. An empty live set stands for "every Pending Export on this item has since been run".
        var model = CausalityModelBuilder.Build(
            CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext(),
            livePendingExportIds: new HashSet<Guid>());

        var pendingExport = model.Roots[0].Children[0].Children[0].Children[0];
        var peLink = pendingExport.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.PendingExport);

        Assert.That(peLink, Is.Not.Null);
        Assert.That(peLink!.Href, Is.EqualTo("/admin/connected-systems/2/pending-exports"));
        Assert.That(peLink.Label, Is.EqualTo("Pending Exports"));
    }

    [Test]
    public void Build_PendingExportStillQueued_LinksTheIndividualRow()
    {
        var model = CausalityModelBuilder.Build(
            CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext(),
            livePendingExportIds: new HashSet<Guid> { CausalityTestData.PendingExportId });

        var pendingExport = model.Roots[0].Children[0].Children[0].Children[0];
        var peLink = pendingExport.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.PendingExport);

        Assert.That(peLink, Is.Not.Null);
        Assert.That(peLink!.Href, Is.EqualTo($"/admin/connected-systems/2/pending-exports/{CausalityTestData.PendingExportId}"));
        Assert.That(peLink.Label, Is.EqualTo("Pending Export"));
    }

    [Test]
    public void Build_WithoutALivePendingExportSet_KeepsTheIndividualLink()
    {
        // Null means "not resolved", not "none are live": callers that cannot run the lookup (tests,
        // and any future caller) must not have every Pending Export link silently downgraded.
        var model = CausalityModelBuilder.Build(
            CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext(),
            livePendingExportIds: null);

        var pendingExport = model.Roots[0].Children[0].Children[0].Children[0];
        var peLink = pendingExport.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.PendingExport);

        Assert.That(peLink!.Href, Is.EqualTo($"/admin/connected-systems/2/pending-exports/{CausalityTestData.PendingExportId}"));
    }

    [Test]
    public void Build_CsoDeletedOutcome_LinksItsDeletionRecordByTheDeletedRecordsId()
    {
        var deletedCsoId = Guid.NewGuid();
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted,
            parent: null, ordinal: 0, targetEntityId: deletedCsoId, targetEntityDescription: "Project-Catalyst");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var deletionLink = model.Roots[0].Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.DeletionRecord);
        Assert.That(deletionLink, Is.Not.Null);
        Assert.That(deletionLink!.Href, Is.EqualTo($"/admin/deleted-objects?cso={deletedCsoId}"));

        // The record is named but not linked: its detail page went with it
        var recordLink = model.Roots[0].Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.Record);
        Assert.That(recordLink, Is.Not.Null);
        Assert.That(recordLink!.Label, Is.EqualTo("Project-Catalyst"));
        Assert.That(recordLink.Href, Is.Null);
    }

    [Test]
    public void Build_CsoDeletedOutcomeWithoutATargetId_LinksTheUnfilteredBrowser()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted,
            parent: null, ordinal: 0);

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var deletionLink = model.Roots[0].Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.DeletionRecord);
        Assert.That(deletionLink, Is.Not.Null);
        Assert.That(deletionLink!.Href, Is.EqualTo("/admin/deleted-objects"));
    }

    [Test]
    public void Build_PendingExportCreatedOutcomeWithoutATargetId_LinksTheSystemQueue()
    {
        // Deprovisioning Pending Exports staged by the Metaverse Object Housekeeping batch can reach the
        // view before their id is known; the link must still take the reader somewhere useful.
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            parent: null, ordinal: 0, targetEntityId: null,
            targetEntityDescription: "Glitterband EMEA", detailCount: 1, detailMessage: "2");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var peLink = model.Roots[0].Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.PendingExport);
        Assert.That(peLink, Is.Not.Null);
        Assert.That(peLink!.Href, Is.EqualTo("/admin/connected-systems/2/pending-exports"));
    }

    [Test]
    public void Build_PendingExportCreatedWithSnapshot_NormalisesSnapshotAttributeRows()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var pendingExport = model.Roots[0].Children[0].Children[0].Children[0];

        Assert.That(pendingExport.AttributeRows, Has.Count.EqualTo(3));
        var displayNameRow = pendingExport.AttributeRows.Single(r => r.Name == "displayName");
        Assert.That(displayNameRow.Operation, Is.EqualTo(CausalityAttributeOperation.Set));
        Assert.That(displayNameRow.Value, Is.EqualTo("Liam Allen"));
        Assert.That(displayNameRow.PreviousValue, Is.Null);
        Assert.That(displayNameRow.TypeAndPlurality, Is.EqualTo("Text · Single-valued"));
    }

    /// <summary>
    /// A queued deprovision is a staged Pending Export, so it must behave exactly as PendingExportCreated
    /// does in the causality graph: downstream lane, its target system linked, its own Pending Export row
    /// linked, and its persisted snapshot rendered as the event's attribute rows.
    /// </summary>
    [Test]
    public void Build_DeprovisionQueuedOutcome_BehavesAsAStagedPendingExport()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var mvoDeleted = model.Roots[0].Children
            .Single(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        var deprovision = mvoDeleted.Children
            .First(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued);

        Assert.That(deprovision.Lane, Is.EqualTo(CausalityLane.Downstream));
        Assert.That(deprovision.SystemId, Is.EqualTo(2),
            "The target system id travels in DetailMessage on both staging outcome types");

        var csLink = deprovision.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.ConnectedSystem);
        Assert.That(csLink, Is.Not.Null);
        Assert.That(csLink!.Href, Is.EqualTo("/admin/connected-systems/2"));

        var peLink = deprovision.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.PendingExport);
        Assert.That(peLink, Is.Not.Null);

        Assert.That(deprovision.AttributeRows, Has.Count.EqualTo(1));
        Assert.That(deprovision.AttributeRows[0].Name, Is.EqualTo("distinguishedName"));
    }

    /// <summary>
    /// The delete Pending Export's single row is the target's DN, carried so the connector can still find
    /// the entry after the Connected System Object is disconnected. Labelled "1 attribute" like every other
    /// event's change set, it read as though a deprovision had merely set one attribute; the caption says
    /// what the rows are instead.
    /// </summary>
    [Test]
    public void Build_DeprovisionQueuedOutcome_CaptionsItsRowsAsTargetIdentification()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var mvoDeleted = model.Roots[0].Children
            .Single(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        var deprovision = mvoDeleted.Children
            .First(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued);

        Assert.That(deprovision.AttributeRowsCaption, Is.EqualTo("Target identified by"));
    }

    [Test]
    public void Build_PendingExportCreatedOutcome_HasNoAttributeRowsCaption()
    {
        // Every other event's rows genuinely are attribute changes, so they keep the plain count label.
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var pendingExport = model.Roots[0].Children[0].Children[0].Children[0];

        Assert.That(pendingExport.AttributeRowsCaption, Is.Null);
    }

    [Test]
    public void Build_MvoDeletedOutcome_CarriesDestructiveBadgeAndDeletionRecordLink()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var outOfScope = model.Roots[0];
        var mvoDeleted = outOfScope.Children.Single(c => c.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);

        Assert.That(mvoDeleted.Badge, Is.EqualTo("Destructive"));
        Assert.That(mvoDeleted.DetailMessage, Is.EqualTo("Deleted immediately: last authoritative source disconnected"));

        var deletionLink = mvoDeleted.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.DeletionRecord);
        Assert.That(deletionLink, Is.Not.Null);
        Assert.That(deletionLink!.Href, Is.EqualTo("/admin/deleted-objects?t=deleted-mvos&mvo=11111111-1111-1111-1111-111111111111"),
            "The deletion record is deep-linked by the deleted Identity's own id, on the Deleted MVOs tab");

        // The deleted Identity is named, but not linked: the Metaverse Object no longer exists
        var identityMention = mvoDeleted.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.Identity);
        Assert.That(identityMention, Is.Not.Null);
        Assert.That(identityMention!.Label, Is.EqualTo("Erin Byrne"));
        Assert.That(identityMention.Href, Is.Null);
    }

    [Test]
    public void Build_OutcomeWithMvoDeletedChild_SuppressesIdentityLinkOnParent()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());
        var outOfScope = model.Roots[0];

        // Parity with OutcomeTreeNode: no Identity link when a child records the Identity's deletion
        Assert.That(outOfScope.Links.Any(l => l.Kind == CausalityEntityKind.Identity && l.Href != null), Is.False);
    }

    [Test]
    public void Build_ExportFailedOutcome_CarriesNeedsAttentionBadgeErrorAndQueuedChangesLink()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.ExportFailureItem(), CausalityTestData.ExportContext());
        var exportFailed = model.Roots[0].Children[0];

        Assert.That(exportFailed.Badge, Is.EqualTo("Needs attention"));
        Assert.That(exportFailed.DetailMessage, Is.EqualTo("LDAP error 50: insufficient access rights"));
        Assert.That(exportFailed.SystemId, Is.EqualTo(2), "Export outcomes belong to the record's own Connected System");

        var peLink = exportFailed.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.PendingExport);
        Assert.That(peLink, Is.Not.Null);
        Assert.That(peLink!.Href, Is.EqualTo("/admin/connected-systems/2/pending-exports"));
    }

    [Test]
    public void Build_ExportFailedOutcome_CrossSystemCascade_BelongsToRecordSystemNotRunSystem()
    {
        // A cascade: the Run Profile executed against Yellowstone APAC (id 1), but this Run Profile
        // Execution Item's own record (and its failed export) belongs to Glitterband EMEA (id 2).
        var model = CausalityModelBuilder.Build(CausalityTestData.ExportFailureItem(), CausalityTestData.CascadeContext());
        var exported = model.Roots[0];
        var exportFailed = exported.Children[0];

        // Both the export attempt and its failure describe the record's own export, so they must be
        // attributed to the record's system, never the run's system this item happens to be filed
        // under for a cross-system cascade.
        Assert.That(exported.SystemId, Is.EqualTo(2), "Export outcomes belong to the record's own Connected System");
        Assert.That(exported.SystemName, Is.EqualTo("Glitterband EMEA"));
        Assert.That(exportFailed.SystemId, Is.EqualTo(2));
        Assert.That(exportFailed.SystemName, Is.EqualTo("Glitterband EMEA"));

        var peLink = exportFailed.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.PendingExport);
        Assert.That(peLink, Is.Not.Null);
        Assert.That(peLink!.Href, Is.EqualTo("/admin/connected-systems/2/pending-exports"),
            "The failed export's Pending Exports link must point at the record's own system, where the queued change actually lives");
    }

    [Test]
    public void Build_SiblingOutcomes_AreOrderedByOrdinal()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated, parent: null, ordinal: 2);
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded, parent: null, ordinal: 1);

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        Assert.That(model.Roots.Select(r => r.OutcomeType), Is.EqualTo(new[]
        {
            ActivityRunProfileExecutionItemSyncOutcomeType.CsoAdded,
            ActivityRunProfileExecutionItemSyncOutcomeType.CsoUpdated
        }));
    }

    [Test]
    public void Build_OrphanedParentReference_TreatsOutcomeAsRoot()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var orphan = CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, parent: null, ordinal: 0);
        orphan.ParentSyncOutcomeId = Guid.NewGuid(); // parent not present in the list

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        Assert.That(model.Roots, Has.Count.EqualTo(1));
        Assert.That(model.Roots[0].OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow));
    }

    [Test]
    public void Build_LegacyOutcomeWithoutSyncRuleAttribution_ProducesNoRuleLink()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
            parent: null, ordinal: 0, targetEntityId: CausalityTestData.MvoId, targetEntityDescription: "Erin Byrne");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        Assert.That(model.Roots[0].Links.Any(l => l.Kind == CausalityEntityKind.SynchronisationRule), Is.False);
    }

    [Test]
    public void Build_SyncRuleNameWithoutId_ProducesUnlinkedRuleLabel()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
            parent: null, ordinal: 0, syncRuleName: "Yellowstone People - Inbound");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        var ruleLink = model.Roots[0].Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.SynchronisationRule);
        Assert.That(ruleLink, Is.Not.Null);
        Assert.That(ruleLink!.Label, Is.EqualTo("Yellowstone People - Inbound"));
        Assert.That(ruleLink.Href, Is.Null);
    }

    [Test]
    public void Build_EmptyOutcomesAndEmptyContext_ProducesEmptyModelWithoutThrowing()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };

        var model = CausalityModelBuilder.Build(item, CausalityTestData.EmptyContext());

        Assert.That(model.Roots, Is.Empty);
        Assert.That(model.AllEvents(), Is.Empty);
    }

    [Test]
    public void Build_OutcomesWithMissingDescriptionsAndEmptyContext_DoesNotThrow()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        foreach (var outcomeType in Enum.GetValues<ActivityRunProfileExecutionItemSyncOutcomeType>())
            CausalityTestData.AddOutcome(item, outcomeType, parent: null, ordinal: (int)outcomeType);

        Assert.That(() => CausalityModelBuilder.Build(item, CausalityTestData.EmptyContext()), Throws.Nothing);
    }

    [Test]
    public void Build_AttributeFlowWithMvoChanges_NormalisesSvaUpdatePairIntoSetWithPreviousValue()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            parent: null, ordinal: 0, detailCount: 1);

        var mvoChange = new MetaverseObjectChange { Id = Guid.NewGuid() };
        var attribute = new MetaverseObjectChangeAttribute
        {
            Id = Guid.NewGuid(),
            AttributeName = "Job Title",
            AttributeType = AttributeDataType.Text,
            Attribute = new MetaverseAttribute { Name = "Job Title", AttributePlurality = AttributePlurality.SingleValued }
        };
        attribute.ValueChanges.Add(new MetaverseObjectChangeAttributeValue { ValueChangeType = ValueChangeType.Add, StringValue = "Senior Analyst" });
        attribute.ValueChanges.Add(new MetaverseObjectChangeAttributeValue { ValueChangeType = ValueChangeType.Remove, StringValue = "Analyst" });
        mvoChange.AttributeChanges.Add(attribute);
        item.MetaverseObjectChange = mvoChange;

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var rows = model.Roots[0].AttributeRows;

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Operation, Is.EqualTo(CausalityAttributeOperation.Set));
        Assert.That(rows[0].Name, Is.EqualTo("Job Title"));
        Assert.That(rows[0].Value, Is.EqualTo("Senior Analyst"));
        Assert.That(rows[0].PreviousValue, Is.EqualTo("Analyst"));
        Assert.That(rows[0].TypeAndPlurality, Is.EqualTo("Text · Single-valued"));
    }

    [Test]
    public void Build_AttributeFlowWithMultiValuedChanges_ProducesAddAndRemoveRows()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            parent: null, ordinal: 0, detailCount: 2);

        var mvoChange = new MetaverseObjectChange { Id = Guid.NewGuid() };
        var attribute = new MetaverseObjectChangeAttribute
        {
            Id = Guid.NewGuid(),
            AttributeName = "Proxy Addresses",
            AttributeType = AttributeDataType.Text,
            Attribute = new MetaverseAttribute { Name = "Proxy Addresses", AttributePlurality = AttributePlurality.MultiValued }
        };
        attribute.ValueChanges.Add(new MetaverseObjectChangeAttributeValue { ValueChangeType = ValueChangeType.Add, StringValue = "smtp:liam@new.example.com" });
        attribute.ValueChanges.Add(new MetaverseObjectChangeAttributeValue { ValueChangeType = ValueChangeType.Remove, StringValue = "smtp:liam@old.example.com" });
        mvoChange.AttributeChanges.Add(attribute);
        item.MetaverseObjectChange = mvoChange;

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var rows = model.Roots[0].AttributeRows;

        Assert.That(rows, Has.Count.EqualTo(2));
        Assert.That(rows.Count(r => r.Operation == CausalityAttributeOperation.Add), Is.EqualTo(1));
        Assert.That(rows.Count(r => r.Operation == CausalityAttributeOperation.Remove), Is.EqualTo(1));
        Assert.That(rows.All(r => r.TypeAndPlurality == "Text · Multi-valued"), Is.True);
    }

    [Test]
    public void Build_ItemWithBothCsoAndMvoChanges_AttributesRowsToTheirOwningEvents()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var outOfScope = CausalityTestData.AddOutcome(item,
            ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
            parent: null, ordinal: 0, syncRuleId: 7, syncRuleName: "Yellowstone People - Inbound");
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            parent: outOfScope, ordinal: 0, detailCount: 1);
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted,
            parent: outOfScope, ordinal: 1);

        item.MetaverseObjectChange = BuildMvoChangeWithSingleRemove("Department", "Retail Ops");
        item.ConnectedSystemObjectChange = BuildCsoChangeWithSingleRemove("departmentNumber", "Retail Ops");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var attributeFlow = model.AllEvents().Single(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        var csoDeleted = model.AllEvents().Single(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.CsoDeleted);

        // The Identity-side event owns the Identity's changes and the record-side event owns the
        // record's; neither shows the combined item-level list (the pill/expander count mismatch)
        Assert.That(attributeFlow.AttributeRows.Select(r => r.Name), Is.EqualTo(new[] { "Department" }));
        Assert.That(csoDeleted.AttributeRows.Select(r => r.Name), Is.EqualTo(new[] { "departmentNumber" }));
    }

    [Test]
    public void Build_ExportedEventWithBothChangeSets_UsesRecordSideRowsOnly()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0, detailCount: 1);

        item.MetaverseObjectChange = BuildMvoChangeWithSingleRemove("Department", "Retail Ops");
        item.ConnectedSystemObjectChange = BuildCsoChangeWithSingleRemove("departmentNumber", "Retail Ops");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        Assert.That(model.Roots[0].AttributeRows.Select(r => r.Name), Is.EqualTo(new[] { "departmentNumber" }));
    }

    [Test]
    public void Build_DeletionDetectedWithCsoChange_CarriesTheRecordRows()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.DeletionDetected,
            parent: null, ordinal: 0);

        item.ConnectedSystemObjectChange = BuildCsoChangeWithSingleRemove("uid", "erin.byrne99");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        Assert.That(model.Roots[0].AttributeRows.Select(r => r.Name), Is.EqualTo(new[] { "uid" }));
    }

    private static MetaverseObjectChange BuildMvoChangeWithSingleRemove(string attributeName, string value)
    {
        var change = new MetaverseObjectChange { Id = Guid.NewGuid() };
        var attribute = new MetaverseObjectChangeAttribute
        {
            Id = Guid.NewGuid(),
            AttributeName = attributeName,
            AttributeType = AttributeDataType.Text,
            Attribute = new MetaverseAttribute { Name = attributeName, AttributePlurality = AttributePlurality.SingleValued }
        };
        attribute.ValueChanges.Add(new MetaverseObjectChangeAttributeValue { ValueChangeType = ValueChangeType.Remove, StringValue = value });
        change.AttributeChanges.Add(attribute);
        return change;
    }

    private static ConnectedSystemObjectChange BuildCsoChangeWithSingleRemove(string attributeName, string value)
    {
        var change = new ConnectedSystemObjectChange { Id = Guid.NewGuid() };
        var attribute = new ConnectedSystemObjectChangeAttribute
        {
            Id = Guid.NewGuid(),
            AttributeName = attributeName,
            AttributeType = AttributeDataType.Text,
            Attribute = new ConnectedSystemObjectTypeAttribute { Name = attributeName, AttributePlurality = AttributePlurality.SingleValued }
        };
        attribute.ValueChanges.Add(new ConnectedSystemObjectChangeAttributeValue { ValueChangeType = ValueChangeType.Remove, StringValue = value });
        change.AttributeChanges.Add(attribute);
        return change;
    }

    [Test]
    public void Build_SingleValuedRemoveOnly_ProducesRemoveRow()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow,
            parent: null, ordinal: 0, detailCount: 1);

        var mvoChange = new MetaverseObjectChange { Id = Guid.NewGuid() };
        var attribute = new MetaverseObjectChangeAttribute
        {
            Id = Guid.NewGuid(),
            AttributeName = "Mobile",
            AttributeType = AttributeDataType.Text,
            Attribute = new MetaverseAttribute { Name = "Mobile", AttributePlurality = AttributePlurality.SingleValued }
        };
        attribute.ValueChanges.Add(new MetaverseObjectChangeAttributeValue { ValueChangeType = ValueChangeType.Remove, StringValue = "0700 900123" });
        mvoChange.AttributeChanges.Add(attribute);
        item.MetaverseObjectChange = mvoChange;

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var rows = model.Roots[0].AttributeRows;

        Assert.That(rows, Has.Count.EqualTo(1));
        Assert.That(rows[0].Operation, Is.EqualTo(CausalityAttributeOperation.Remove));
        Assert.That(rows[0].Value, Is.EqualTo("0700 900123"));
    }
}
