// Copyright (c) Tetron Limited. All rights reserved.
// Licensed under the Tetron Commercial License. See LICENSE file in the project root.

using System;
using System.Linq;
using JIM.Models.Activities;
using JIM.Models.Transactional;
using JIM.Web.Models.Api;
using NUnit.Framework;

namespace JIM.Web.Api.Tests;

/// <summary>
/// Tests for the Sync Preview response DTOs (#1519) and their FromModel mappers, including the recursive
/// outcome tree.
/// </summary>
[TestFixture]
public class SyncPreviewResponseDtoTests
{
    [Test]
    public void FromModel_MapsScalarFieldsAndBlockingFlag()
    {
        var model = new SyncPreviewResult
        {
            Errors = [new SyncPreviewMessage { Code = SyncPreviewMessageCode.ObjectNotFound, Detail = "gone" }],
            Warnings = [new SyncPreviewMessage { Code = SyncPreviewMessageCode.OutOfScope, Detail = "out" }],
            AffectedSyncRules = [new SyncPreviewSyncRuleReference { Id = 7, Name = "Export to AD" }]
        };

        var dto = SyncPreviewResponse.FromModel(model);

        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.HasBlockingErrors, Is.True);
            Assert.That(dto.Errors, Has.Count.EqualTo(1));
            Assert.That(dto.Errors[0].Code, Is.EqualTo(SyncPreviewMessageCode.ObjectNotFound));
            Assert.That(dto.Errors[0].Detail, Is.EqualTo("gone"));
            Assert.That(dto.Warnings, Has.Count.EqualTo(1));
            Assert.That(dto.Warnings[0].Code, Is.EqualTo(SyncPreviewMessageCode.OutOfScope));
            Assert.That(dto.AffectedSyncRules, Has.Count.EqualTo(1));
            Assert.That(dto.AffectedSyncRules[0].Id, Is.EqualTo(7));
            Assert.That(dto.AffectedSyncRules[0].Name, Is.EqualTo("Export to AD"));
        }
    }

    [Test]
    public void FromModel_NullInbound_MapsToNull()
    {
        var dto = SyncPreviewResponse.FromModel(new SyncPreviewResult { Inbound = null });

        Assert.That(dto.Inbound, Is.Null);
    }

    [Test]
    public void FromModel_InboundPresent_MapsAttributeFlowChanges()
    {
        var model = new SyncPreviewResult
        {
            Inbound = new SyncPreviewInboundSummary
            {
                WouldProject = true,
                ProjectedMetaverseObjectTypeId = 3,
                ProjectedMetaverseObjectTypeName = "Person",
                AttributeFlowChanges =
                [
                    new SyncPreviewAttributeFlowChange
                    {
                        AttributeId = 10,
                        AttributeName = "mail",
                        IsAddition = true,
                        Value = "jdoe@example.com",
                        SyncRuleId = 1,
                        SyncRuleName = "Import from HR"
                    }
                ]
            }
        };

        var dto = SyncPreviewResponse.FromModel(model);

        Assert.That(dto.Inbound, Is.Not.Null);
        using (Assert.EnterMultipleScope())
        {
            Assert.That(dto.Inbound!.WouldProject, Is.True);
            Assert.That(dto.Inbound.ProjectedMetaverseObjectTypeName, Is.EqualTo("Person"));
            Assert.That(dto.Inbound.AttributeFlowChanges, Has.Count.EqualTo(1));
            Assert.That(dto.Inbound.AttributeFlowChanges[0].AttributeName, Is.EqualTo("mail"));
            Assert.That(dto.Inbound.AttributeFlowChanges[0].IsAddition, Is.True);
            Assert.That(dto.Inbound.AttributeFlowChanges[0].Value, Is.EqualTo("jdoe@example.com"));
        }
    }

    [Test]
    public void FromModel_OutcomeTree_PreservesRecursiveShapeAndOrder()
    {
        var child1 = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.DeprovisionQueued,
            TargetEntityDescription = "Target A",
            StagedChangeType = PendingExportChangeType.Delete,
            Ordinal = 0
        };
        var child2 = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.Disconnected,
            TargetEntityDescription = "Target B",
            Ordinal = 1
        };
        var root = new SyncOutcomeNode
        {
            OutcomeType = ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted,
            DetailMessage = "WhenLastConnectorDisconnected",
            Children = [child1, child2]
        };

        var dto = SyncPreviewResponse.FromModel(new SyncPreviewResult { OutcomeTree = [root] });

        Assert.That(dto.OutcomeTree, Has.Count.EqualTo(1));
        var rootDto = dto.OutcomeTree[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(rootDto.OutcomeType, Is.EqualTo(ActivityRunProfileExecutionItemSyncOutcomeType.MvoDeleted));
            Assert.That(rootDto.DetailMessage, Is.EqualTo("WhenLastConnectorDisconnected"));
            Assert.That(rootDto.Children, Has.Count.EqualTo(2));
            Assert.That(rootDto.Children[0].TargetEntityDescription, Is.EqualTo("Target A"));
            Assert.That(rootDto.Children[0].StagedChangeType, Is.EqualTo(PendingExportChangeType.Delete));
            Assert.That(rootDto.Children[1].TargetEntityDescription, Is.EqualTo("Target B"));
            Assert.That(rootDto.Children[1].Children, Is.Empty);
        }
    }

    [Test]
    public void FromModel_ProposedExports_MapsPendingExportFields()
    {
        var mvoId = Guid.NewGuid();
        var model = new SyncPreviewResult
        {
            Outbound = new ExportEvaluationPreviewResult
            {
                ProposedExports =
                [
                    new PendingExport
                    {
                        ConnectedSystemId = 4,
                        ChangeType = PendingExportChangeType.Create,
                        SourceMetaverseObjectId = mvoId,
                        AttributeValueChanges =
                        [
                            new PendingExportAttributeValueChange
                            {
                                AttributeId = 20,
                                ChangeType = PendingExportAttributeChangeType.Add,
                                StringValue = "jdoe"
                            }
                        ]
                    }
                ]
            }
        };

        var dto = SyncPreviewResponse.FromModel(model);

        Assert.That(dto.ProposedExports, Has.Count.EqualTo(1));
        var export = dto.ProposedExports[0];
        using (Assert.EnterMultipleScope())
        {
            Assert.That(export.ConnectedSystemId, Is.EqualTo(4));
            Assert.That(export.ChangeType, Is.EqualTo(PendingExportChangeType.Create));
            Assert.That(export.SourceMetaverseObjectId, Is.EqualTo(mvoId));
            Assert.That(export.AttributeChanges, Has.Count.EqualTo(1));
            Assert.That(export.AttributeChanges[0].StringValue, Is.EqualTo("jdoe"));
        }
    }
}
