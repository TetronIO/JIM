// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Collections.Generic;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.Models.Sync;
using JIM.Models.Transactional;
using JIM.Web.Causality;
using NUnit.Framework;

namespace JIM.Web.Tests;

/// <summary>
/// Tests for <see cref="CausalityModelBuilder.BuildSpeculative"/>: the speculative rendering path a
/// Sync Preview result builds through (#1519, D-S1), sharing the recorded tree's event display, lane
/// and operation logic while sourcing links and attribute rows from the preview's own decision records.
/// </summary>
[TestFixture]
public class CausalityModelBuilderSpeculativeTests
{
    private static readonly Guid MvoId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static CausalityPageContext Context() => new(
        ConnectedSystemId: 1,
        ConnectedSystemName: "Yellowstone APAC",
        RunProfileName: "Full Synchronisation",
        CsoId: Guid.NewGuid(),
        CsoConnectedSystemId: 1,
        CsoConnectedSystemName: "Yellowstone APAC",
        CsoDisplayName: "Liam Allen",
        CsoExternalId: "S8-287551",
        CsoObjectTypeName: "person",
        MvoTypeName: "Person",
        MvoTypePluralName: "People");

    [Test]
    public void BuildSpeculative_AnyPreview_SetsIsSpeculativeTrue()
    {
        var preview = new SyncPreviewResult();

        var model = CausalityModelBuilder.BuildSpeculative(preview, Context());

        Assert.That(model.IsSpeculative, Is.True);
    }

    [Test]
    public void BuildSpeculative_ProjectedNode_UsesConditionalMoodLabel()
    {
        var preview = new SyncPreviewResult
        {
            OutcomeTree = [new SyncOutcomeNode { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Projected }]
        };

        var model = CausalityModelBuilder.BuildSpeculative(preview, Context());

        Assert.That(model.Roots[0].Label, Is.EqualTo("A Metaverse Object would be projected"));
    }

    [Test]
    public void BuildSpeculative_MvoDeletedNode_LinksTheLiveMetaverseObjectNotADeletionRecord()
    {
        // Unlike the recorded tree, nothing is actually deleted by a preview: the Metaverse Object mention
        // must point at its live page, not the "View deletion record" href the recorded MvoDeleted uses.
        var preview = new SyncPreviewResult
        {
            OutcomeTree =
            [
                new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
                    TargetEntityId = MvoId,
                    TargetEntityDescription = "Liam Allen"
                }
            ]
        };

        var model = CausalityModelBuilder.BuildSpeculative(preview, Context());

        var identityLink = model.Roots[0].Links.Single(l => l.Kind == CausalityEntityKind.Identity);
        Assert.That(identityLink.Href, Is.EqualTo("/t/people/v/55555555-5555-5555-5555-555555555555"));
    }

    [Test]
    public void BuildSpeculative_CascadeTree_PreservesShapeAndUsesSpeculativeLabelsThroughout()
    {
        // The Phase 1 cascade shape: DisconnectedOutOfScope -> MvoDeleted -> DeprovisionQueued.
        var deprovisionNode = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
            TargetEntityDescription = "Glitterband EMEA",
            DetailMessage = "2",
            StagedChangeType = PendingExportChangeType.Delete
        };
        var mvoDeletedNode = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
            TargetEntityId = MvoId,
            TargetEntityDescription = "Liam Allen",
            Children = [deprovisionNode]
        };
        var rootNode = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope,
            TargetEntityId = MvoId,
            TargetEntityDescription = "Liam Allen",
            Children = [mvoDeletedNode]
        };
        var preview = new SyncPreviewResult { OutcomeTree = [rootNode] };

        var model = CausalityModelBuilder.BuildSpeculative(preview, Context());

        using (Assert.EnterMultipleScope())
        {
            var root = model.Roots[0];
            Assert.That(root.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.DisconnectedOutOfScope));
            Assert.That(root.Label, Is.EqualTo("Would be disconnected from its Metaverse Object"));

            var deleted = root.Children[0];
            Assert.That(deleted.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted));
            Assert.That(deleted.Label, Is.EqualTo("The Metaverse Object would be deleted"));

            var deprovision = deleted.Children[0];
            Assert.That(deprovision.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued));
            Assert.That(deprovision.Label, Is.EqualTo("Would be deprovisioned from its target Connected System"));
            Assert.That(deprovision.Lane, Is.EqualTo(CausalityLane.Downstream));
            Assert.That(deprovision.SystemId, Is.EqualTo(2));
        }
    }

    [Test]
    public void BuildSpeculative_AttributeFlowNode_RendersInboundFlowChangesAsAttributeRows()
    {
        var preview = new SyncPreviewResult
        {
            Inbound = new SyncPreviewInboundSummary
            {
                AttributeFlowChanges =
                [
                    new SyncPreviewAttributeFlowChange { AttributeName = "department", IsAddition = true, Value = "Engineering" }
                ]
            },
            OutcomeTree = [new SyncOutcomeNode { OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.AttributeFlow, DetailCount = 1 }]
        };

        var model = CausalityModelBuilder.BuildSpeculative(preview, Context());

        Assert.That(model.Roots[0].AttributeRows, Has.Count.EqualTo(1));
        Assert.That(model.Roots[0].AttributeRows[0].Name, Is.EqualTo("department"));
        Assert.That(model.Roots[0].AttributeRows[0].Value, Is.EqualTo("Engineering"));
    }

    [Test]
    public void BuildSpeculative_PendingExportCreatedNode_CorrelatesAttributeChangesBySyncRuleId()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 7, Name = "mail", Type = AttributeDataType.Text };
        var preview = new SyncPreviewResult
        {
            OutboundDecisions = new OutboundPreviewResult
            {
                Entries =
                [
                    new OutboundPreviewEntry
                    {
                        Kind = OutboundPreviewEntryKind.Staging,
                        SyncRuleId = 42,
                        SyncRuleName = "Export to Glitterband",
                        ConnectedSystemId = 2,
                        EffectiveChangeType = PendingExportChangeType.Update,
                        AttributeChanges =
                        [
                            new PendingExportAttributeValueChange
                            {
                                Attribute = attribute,
                                AttributeId = attribute.Id,
                                StringValue = "liam.allen@example.com",
                                ChangeType = PendingExportAttributeChangeType.Update
                            }
                        ]
                    }
                ]
            },
            OutcomeTree =
            [
                new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.PendingExportCreated,
                    SyncRuleId = 42,
                    SyncRuleName = "Export to Glitterband",
                    DetailMessage = "2",
                    DetailCount = 1,
                    StagedChangeType = PendingExportChangeType.Update
                }
            ]
        };

        var model = CausalityModelBuilder.BuildSpeculative(preview, Context());

        var node = model.Roots[0];
        Assert.That(node.AttributeRows, Has.Count.EqualTo(1));
        Assert.That(node.AttributeRows[0].Name, Is.EqualTo("mail"));
        Assert.That(node.AttributeRows[0].Value, Is.EqualTo("liam.allen@example.com"));
    }

    [Test]
    public void BuildSpeculative_CascadeDeprovisionNodeWithNoSyncRule_CorrelatesByConnectedSystemId()
    {
        var attribute = new ConnectedSystemObjectTypeAttribute { Id = 9, Name = "distinguishedName", Type = AttributeDataType.Text };
        var deleteExport = new PendingExport
        {
            ChangeType = PendingExportChangeType.Delete,
            ConnectedSystemId = 3,
            AttributeValueChanges =
            [
                new PendingExportAttributeValueChange
                {
                    Attribute = attribute,
                    AttributeId = attribute.Id,
                    StringValue = "cn=liam,dc=example",
                    ChangeType = PendingExportAttributeChangeType.Update
                }
            ]
        };
        var preview = new SyncPreviewResult
        {
            Outbound = new ExportEvaluationPreviewResult { ProposedExports = [deleteExport] },
            OutcomeTree =
            [
                new SyncOutcomeNode
                {
                    OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
                    TargetEntityDescription = "Directory EU",
                    DetailMessage = "3",
                    StagedChangeType = PendingExportChangeType.Delete
                }
            ]
        };

        var model = CausalityModelBuilder.BuildSpeculative(preview, Context());

        var node = model.Roots[0];
        Assert.That(node.AttributeRows, Has.Count.EqualTo(1));
        Assert.That(node.AttributeRows[0].Name, Is.EqualTo("distinguishedName"));
    }

    [Test]
    public void BuildSpeculative_EmptyOutcomeTree_ProducesNoRoots()
    {
        var model = CausalityModelBuilder.BuildSpeculative(new SyncPreviewResult(), Context());

        Assert.That(model.Roots, Is.Empty);
    }
}
