// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Activities.DTOs;
using JIM.Models.Core;
using JIM.Models.Enums;
using JIM.Models.Staging;
using JIM.Models.Sync;
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
            [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionCancelled] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.ValuesPreserved] = CausalityLane.Identity,

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
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldChangeDeprovisionAction] = CausalityLane.Identity,
            // A mapping that would not evaluate leaves a Metaverse Object attribute unwritten, so it belongs beside
            // the other Metaverse-side transitions rather than in the export-side Downstream lane.
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldFailAttributeFlow] = CausalityLane.Identity,
            // Every Object Matching transition decides which Metaverse Object an account belongs to, which is the
            // Identity lane's whole subject.
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinDifferentMetaverseObject] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldJoinInsteadOfProject] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldProjectInsteadOfJoin] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldMatchAmbiguously] = CausalityLane.Identity,
            // Projecting decides whether an identity exists at all, so it is Metaverse-side; the other two are
            // about what reaches, or stops reaching, the target system.
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProjecting] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopProvisioning] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopCorrectingDrift] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldStopBeingImported] = CausalityLane.Source,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldResumeBeingImported] = CausalityLane.Source,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldWithdrawContributedValues] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldRetainContributedValues] = CausalityLane.Identity,
            // An export rule's scope decides what reaches the target system, so its transitions sit beside the
            // other export-side previews rather than with the import-side scope pair above.
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldLeaveExportScope] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.WouldEnterExportScope] = CausalityLane.Downstream
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

    /// <summary>
    /// The execution-side sibling of the queued case: when the delete Pending Export is carried out, the
    /// Deprovisioned item's change snapshot still holds only the target's DN. Counted as a change, the page
    /// reported the deletion as having "Set" one attribute; the rows identify the deleted entry, so they take
    /// the same caption as the queueing event.
    /// </summary>
    [Test]
    public void Build_DeprovisionedExecutionOutcome_CaptionsItsRowsAsTargetIdentification()
    {
        var item = new ActivityRunProfileExecutionItem
        {
            Id = Guid.NewGuid(),
            ObjectChangeType = ObjectChangeType.Deprovisioned,
            ConnectedSystemObjectChange = CausalityTestData.BuildDeprovisionTargetSnapshot()
        };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned,
            parent: null, ordinal: 0);

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());
        var deprovisioned = model.Roots
            .Single(r => r.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned);

        Assert.That(deprovisioned.AttributeRowsCaption, Is.EqualTo("Target identified by"));
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

    #region the Deletion Rule that declined (#1223 Phase 1e)

    /// <summary>
    /// An item whose Deletion Rule evaluated on a disconnection: outcomes were recorded, but none of them is a
    /// Metaverse Object deletion.
    /// </summary>
    private static ActivityRunProfileExecutionItem DisconnectedItem()
    {
        var item = CausalityTestData.NewJoinerItem();
        item.ObjectChangeType = ObjectChangeType.Disconnected;
        return item;
    }

    private static MvoDeletionPolicySnapshot Snapshot() => new()
    {
        DeletionRule = MetaverseObjectDeletionRule.WhenAuthoritativeSourceDisconnected,
        TriggerMode = AuthoritativeSourceTriggerMode.AllSourcesDisconnect,
        SelectedSourceSystemNames = ["HR", "Payroll"],
        RemainingConnectedSourceSystemNames = ["Payroll"]
    };

    /// <summary>
    /// The one thing the causality views structurally cannot derive from outcomes. A Deletion Rule that
    /// evaluates and declines records nothing, because nothing happened; without a synthetic event the page
    /// says only that the record disconnected and leaves the reader to wonder what became of the Identity.
    /// </summary>
    [Test]
    public void Build_SyncDisconnectionWhereTheDeletionRuleDeclined_AddsASyntheticIdentityEvent()
    {
        var model = CausalityModelBuilder.Build(DisconnectedItem(), CausalityTestData.NewJoinerContext(),
            deletionPolicySnapshot: Snapshot(), isSynchronisationRun: true);

        var synthetic = model.AllEvents().SingleOrDefault(e => e.IsSynthetic);

        Assert.That(synthetic, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(synthetic!.Lane, Is.EqualTo(CausalityLane.Identity));
            Assert.That(synthetic!.OutcomeType, Is.Null, "nothing was recorded, so there is no outcome to name");
            Assert.That(synthetic!.PlainLabel, Is.EqualTo("Identity not deleted"));
            Assert.That(synthetic!.TechnicalLabel, Is.EqualTo("Metaverse Object not deleted"));
        }
    }

    [Test]
    public void Build_ImportDisconnection_AddsNoSyntheticEvent()
    {
        // Only a Synchronisation evaluates the Deletion Rule. An import detects the deletion and stops, so
        // there is no decision yet to report and claiming one would be a lie about what the run did.
        var model = CausalityModelBuilder.Build(DisconnectedItem(), CausalityTestData.NewJoinerContext(),
            deletionPolicySnapshot: Snapshot(), isSynchronisationRun: false);

        Assert.That(model.AllEvents().Any(e => e.IsSynthetic), Is.False);
    }

    [Test]
    public void Build_WithNoPolicySnapshot_AddsNoSyntheticEvent()
    {
        // The snapshot is the only supported source of the explanation: it records the rule as it was in force
        // at decision time, and there is deliberately no fallback to the object type's current configuration.
        var model = CausalityModelBuilder.Build(DisconnectedItem(), CausalityTestData.NewJoinerContext(),
            deletionPolicySnapshot: null, isSynchronisationRun: true);

        Assert.That(model.AllEvents().Any(e => e.IsSynthetic), Is.False);
    }

    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted)]
    [TestCase(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled)]
    public void Build_WhereTheDeletionRuleFired_AddsNoSyntheticEvent(
        ActivityRunProfileExecutionItemSyncOutcomeType outcomeType)
    {
        var item = DisconnectedItem();
        item.SyncOutcomes.Add(new ActivityRunProfileExecutionItemSyncOutcome
        {
            Id = Guid.NewGuid(),
            OutcomeType = outcomeType,
            Ordinal = item.SyncOutcomes.Count
        });

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext(),
            deletionPolicySnapshot: Snapshot(), isSynchronisationRun: true);

        Assert.That(model.AllEvents().Any(e => e.IsSynthetic), Is.False,
            "the deletion happened, so the recorded outcome is the answer and a synthetic one would contradict it");
    }

    [Test]
    public void Build_SyntheticEvent_IsExcludedFromTheOutcomePills()
    {
        // The strip counts what the run recorded. A pill for something it decided not to do would read as an
        // outcome, and the count beside it would be a count of non-events.
        var model = CausalityModelBuilder.Build(DisconnectedItem(), CausalityTestData.NewJoinerContext(),
            deletionPolicySnapshot: Snapshot(), isSynchronisationRun: true);

        var summary = CausalitySummaryBuilder.Build(model);

        Assert.That(summary.Pills.Count, Is.EqualTo(
            CausalitySummaryBuilder.Build(CausalityModelBuilder.Build(
                DisconnectedItem(), CausalityTestData.NewJoinerContext())).Pills.Count));
    }

    #endregion

    /// <summary>
    /// A Metaverse Object link is only ever built where the object's type plural name is known, because
    /// that is what the route is keyed on (<c>/t/{plural}/v/{id}</c>). The fallback used to invent
    /// <c>/identity/search/{id}</c>, which is not a route in this application and never has been, so on
    /// any item whose type could not be resolved (a synchronisation whose record has since been deleted
    /// is the common one) every Identity on the panel pointed at a page that does not exist. An unlinked
    /// name is the honest answer: a link that goes nowhere reads as an affordance and is not one.
    /// </summary>
    [Test]
    public void Build_IdentityLinkWithNoObjectTypePluralName_RendersUnlinkedRatherThanAnInventedRoute()
    {
        var context = CausalityTestData.NewJoinerContext() with { MvoTypePluralName = null };

        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), context);

        var identityLinks = model.AllEvents()
            .SelectMany(e => e.Links)
            .Where(l => l.Kind == CausalityEntityKind.Identity)
            .ToList();
        Assert.That(identityLinks, Is.Not.Empty, "the new joiner story names its Identity");
        Assert.That(identityLinks.Select(l => l.Href), Has.All.Null);
    }

    /// <summary>
    /// The other half: where the plural name is known the link is the real route, so suppressing the
    /// invented one costs nothing that worked.
    /// </summary>
    [Test]
    public void Build_IdentityLinkWithAnObjectTypePluralName_UsesTheMetaverseObjectRoute()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var identityLink = model.AllEvents()
            .SelectMany(e => e.Links)
            .First(l => l.Kind == CausalityEntityKind.Identity && l.Href != null);
        Assert.That(identityLink.Href, Does.StartWith("/t/people/v/"));
    }

    #region Operation chip (#1495 follow-up)

    [Test]
    public void Build_NewJoinerScenario_PopulatesOperationForEventsThatStateAnObjectOperation()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());

        var projected = model.AllEvents().First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Projected);
        var attributeFlow = model.AllEvents().First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow);
        var provisioned = model.AllEvents().First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Provisioned);
        var pendingExport = model.AllEvents().First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(projected.Operation?.PlainLabel, Is.EqualTo("Created"));
            Assert.That(attributeFlow.Operation?.PlainLabel, Is.EqualTo("Updated"));
            Assert.That(provisioned.Operation?.PlainLabel, Is.EqualTo("Created"));
            // The fixture's Pending Export was staged as a Create (#1561 follow-up); the outcome's
            // recorded StagedChangeType is what now tells Create and Update apart.
            Assert.That(pendingExport.Operation?.PlainLabel, Is.EqualTo("Created"));
        }
    }

    /// <summary>
    /// An Export queued outcome recorded before the staged kind was captured (StagedChangeType null)
    /// must carry no operation chip: guessing Create would be dishonest for what could equally have
    /// been an Update.
    /// </summary>
    [Test]
    public void Build_PendingExportCreatedWithNoStagedChangeType_HasNoOperation()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
            parent: null, ordinal: 0, targetEntityDescription: "Glitterband EMEA");

        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext());

        Assert.That(model.AllEvents().Single().Operation, Is.Null);
    }

    [Test]
    public void Build_LeaverScenario_PopulatesOperationForTheDeletionAndTheStagedDeprovisions()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.LeaverItem(), CausalityTestData.NewJoinerContext());

        var mvoDeleted = model.AllEvents().First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted);
        var deprovisions = model.AllEvents()
            .Where(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued)
            .ToList();
        var outOfScope = model.AllEvents().First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(mvoDeleted.Operation?.PlainLabel, Is.EqualTo("Deleted"));
            Assert.That(deprovisions, Has.Count.EqualTo(2));
            Assert.That(deprovisions.Select(d => d.Operation?.PlainLabel), Has.All.EqualTo("Deleted"));
            Assert.That(outOfScope.Operation, Is.Null, "leaving scope is not itself an object operation");
        }
    }

    [Test]
    public void Build_ExportedWithAResolvedChainDecision_PopulatesOperationFromTheDecision()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        var exported = CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.Exported,
            parent: null, ordinal: 0, detailCount: 3);
        var cohort = CausalityTestData.Cohort(
            CausalEdgeType.PendingExportQueueingCausedExportExecution,
            reasonCode: CausalReasonCode.ExportCreateStaged,
            effectSyncOutcomeId: exported.Id);
        var chain = CausalityTestData.Chain(item.Id, false, cohort);

        var model = CausalityModelBuilder.Build(item, CausalityTestData.ExportContext(), chain: chain);

        var exportedEvent = model.AllEvents().First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(exportedEvent.Operation?.PlainLabel, Is.EqualTo("Created"));
            Assert.That(exportedEvent.Operation?.TechnicalLabel, Is.EqualTo("Export Staged (Create)"));
            Assert.That(exportedEvent.Operation?.Tone, Is.EqualTo(CausalityTone.Success));
        }
    }

    [Test]
    public void Build_ExportedWithNoChain_LeavesOperationNullRatherThanGuessed()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.ExportFailureItem(), CausalityTestData.ExportContext());

        var exportedEvent = model.AllEvents().First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.Exported);
        var failedEvent = model.AllEvents().First(e => e.OutcomeType == ActivityRunProfileExecutionItemSyncOutcomeType.ExportFailed);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(exportedEvent.Operation, Is.Null);
            Assert.That(failedEvent.Operation, Is.Null);
        }
    }

    /// <summary>
    /// A Configuration Change Preview transition (#827) never executes, so its event states no object
    /// operation: nothing happened. Proven end-to-end through the builder, not just at the map (see
    /// OutcomeDisplayMapEventOperationTests), because a preview item is a genuinely different shape (no
    /// chain, no attribute changes) and the builder must not derive an operation from anything else on
    /// the event.
    /// </summary>
    [Test]
    public void Build_PreviewOutcome_CarriesNoOperation()
    {
        var item = new ActivityRunProfileExecutionItem { Id = Guid.NewGuid() };
        CausalityTestData.AddOutcome(item, ActivityRunProfileExecutionItemSyncOutcomeType.WouldFallOutOfScope,
            parent: null, ordinal: 0);

        var model = CausalityModelBuilder.Build(item, CausalityTestData.NewJoinerContext());

        Assert.That(model.AllEvents().Single().Operation, Is.Null);
    }

    #endregion
}
