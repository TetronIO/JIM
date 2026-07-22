// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Enums;
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
            [ActivityRunProfileExecutionItemSyncOutcomeType.Exported] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.Deprovisioned] = CausalityLane.Downstream,
            [ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeletionScheduled] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.AssertedNull] = CausalityLane.Identity,
            [ActivityRunProfileExecutionItemSyncOutcomeType.NoContributor] = CausalityLane.Identity
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

        var peLink = pendingExport.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.PendingExport);
        Assert.That(peLink, Is.Not.Null);
        Assert.That(peLink!.Href, Is.EqualTo("/admin/connected-systems/2/pending-exports"));

        // The PendingExportCreated target entity is a Pending Export id, never an Identity
        Assert.That(pendingExport.Links.Any(l => l.Kind == CausalityEntityKind.Identity), Is.False);
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
        Assert.That(deletionLink!.Href, Is.EqualTo("/admin/deleted-objects"));

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
        Assert.That(exportFailed.SystemId, Is.EqualTo(2), "Export outcomes belong to the page's Connected System");

        var peLink = exportFailed.Links.SingleOrDefault(l => l.Kind == CausalityEntityKind.PendingExport);
        Assert.That(peLink, Is.Not.Null);
        Assert.That(peLink!.Href, Is.EqualTo("/admin/connected-systems/2/pending-exports"));
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

        Assert.DoesNotThrow(() => CausalityModelBuilder.Build(item, CausalityTestData.EmptyContext()));
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

    [Test]
    public void Build_EventSentenceSegments_LeadWithPlainLabelText()
    {
        var model = CausalityModelBuilder.Build(CausalityTestData.NewJoinerItem(), CausalityTestData.NewJoinerContext());
        var projected = model.Roots[0];

        Assert.That(projected.SentenceSegments, Is.Not.Empty);
        var first = projected.SentenceSegments[0] as SummarySegment.Text;
        Assert.That(first, Is.Not.Null);
        Assert.That(first!.Value, Does.StartWith("Identity created"));
    }
}
